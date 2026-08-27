namespace dotnet_demo.ServiceKit.Topology;

/// <summary>A unit of simulated work inside a route. Each one becomes a span.</summary>
public abstract record WorkStep(string Name);

/// <summary>A data store round-trip, tagged with db.* semantic conventions.</summary>
public sealed record DbStep(string Operation, string Table, string Statement, int MinMs, int MaxMs, string System = "postgresql")
    : WorkStep($"{Operation} {Table}");

/// <summary>An HTTP call to another service in the platform. This is what stitches traces together.</summary>
public sealed record CallStep(string TargetService, string Path, bool Optional = false)
    : WorkStep($"call {TargetService}");

/// <summary>Local CPU work.</summary>
public sealed record ComputeStep(string Label, int MinMs, int MaxMs) : WorkStep(Label);

/// <summary>Cache lookup with a hit rate; a miss costs extra latency.</summary>
public sealed record CacheStep(string Key, double HitRate, int MinMs = 1, int MaxMs = 6) : WorkStep($"cache {Key}");

/// <summary>Publish to a broker — modelled as a producer span.</summary>
public sealed record QueueStep(string Queue, int MinMs = 2, int MaxMs = 15) : WorkStep($"publish {Queue}");

public sealed record RouteDefinition(
    string Path,
    string Operation,
    IReadOnlyList<WorkStep> Steps,
    double FailureRate = 0.0,
    string? FailureMessage = null);

public sealed record ServiceDefinition(
    string Name,
    int Port,
    string Description,
    string Tier,
    IReadOnlyList<RouteDefinition> Routes,
    bool IsEntryPoint = false);

/// <summary>
/// The platform: 16 services modelling a Medicaid claims pipeline, plus the standalone
/// order service. Ports are 6001-6016; the legacy .NET Framework adapter is 6016 and is a
/// separate project, listed here only so its callers can resolve its address.
///
/// A single claim submitted at the gateway fans out across 12+ services and produces a
/// trace with 60+ spans:
///
///   api-gateway
///     └─ auth-service ─ member-service ─ legacy-mainframe-adapter
///     └─ claims-intake
///          ├─ member-service ─ legacy-mainframe-adapter
///          ├─ provider-service
///          └─ claims-validation
///               ├─ eligibility-service ─ legacy-mainframe-adapter
///               └─ benefits-service ─ pricing-service ─ adjudication-service
///                                                        ├─ payment-service
///                                                        │    ├─ notification-service
///                                                        │    └─ document-service
///                                                        └─ audit-service
/// </summary>
public static class ServiceCatalog
{
    public const string LegacyAdapterName = "dotnet_demo-legacy-mainframe-adapter";
    public const int LegacyAdapterPort = 6016;

    public static readonly IReadOnlyList<ServiceDefinition> Services = new List<ServiceDefinition>
    {
        // ---- edge -----------------------------------------------------------
        new("dotnet_demo-api-gateway", 6001, "Public edge: authenticates and routes claim traffic.", "edge",
            new[]
            {
                new RouteDefinition("/claims/submit", "SubmitClaim", new WorkStep[]
                {
                    new CallStep("dotnet_demo-auth-service", "/token/validate"),
                    new ComputeStep("request.normalize", 1, 6),
                    new CallStep("dotnet_demo-claims-intake", "/claims/accept"),
                }),
                new RouteDefinition("/members/lookup", "LookupMember", new WorkStep[]
                {
                    new CallStep("dotnet_demo-auth-service", "/token/validate"),
                    new CallStep("dotnet_demo-member-service", "/members/profile"),
                }),
                new RouteDefinition("/reports/daily", "DailyReport", new WorkStep[]
                {
                    new CallStep("dotnet_demo-auth-service", "/token/validate"),
                    new CallStep("dotnet_demo-reporting-service", "/reports/build"),
                }),
            }, IsEntryPoint: true),

        // ---- identity -------------------------------------------------------
        new("dotnet_demo-auth-service", 6002, "Token validation and entitlement checks.", "platform",
            new[]
            {
                new RouteDefinition("/token/validate", "ValidateToken", new WorkStep[]
                {
                    new CacheStep("jwks", 0.85),
                    new ComputeStep("jwt.verify", 1, 5),
                    new DbStep("SELECT", "sessions", "SELECT * FROM sessions WHERE token_hash = $1", 2, 10),
                }, FailureRate: 0.02, FailureMessage: "token signature validation failed"),
            }),

        // ---- master data ----------------------------------------------------
        new("dotnet_demo-member-service", 6003, "Member demographics and coverage master data.", "domain",
            new[]
            {
                new RouteDefinition("/members/profile", "GetMemberProfile", new WorkStep[]
                {
                    new CacheStep("member", 0.6),
                    new DbStep("SELECT", "members", "SELECT * FROM members WHERE member_id = $1", 4, 22),
                    new CallStep(LegacyAdapterName, "/mainframe/member"),
                }),
            }),

        new("dotnet_demo-provider-service", 6004, "Provider registry, NPI validation, network status.", "domain",
            new[]
            {
                new RouteDefinition("/providers/verify", "VerifyProvider", new WorkStep[]
                {
                    new DbStep("SELECT", "providers", "SELECT * FROM providers WHERE npi = $1", 4, 18),
                    new ComputeStep("npi.checksum", 1, 3),
                }, FailureRate: 0.03, FailureMessage: "provider not in network"),
            }),

        // ---- claims pipeline ------------------------------------------------
        new("dotnet_demo-claims-intake", 6005, "Accepts claims, assigns control numbers, fans out.", "domain",
            new[]
            {
                new RouteDefinition("/claims/accept", "AcceptClaim", new WorkStep[]
                {
                    new ComputeStep("edi.837.parse", 3, 18),
                    new DbStep("INSERT", "claims", "INSERT INTO claims (icn, member_id, npi, total) VALUES ($1,$2,$3,$4)", 4, 20),
                    new CallStep("dotnet_demo-member-service", "/members/profile"),
                    new CallStep("dotnet_demo-provider-service", "/providers/verify"),
                    new CallStep("dotnet_demo-claims-validation", "/claims/validate"),
                    new QueueStep("claims.accepted"),
                }),
            }),

        new("dotnet_demo-claims-validation", 6006, "Structural and clinical edits on submitted claims.", "domain",
            new[]
            {
                new RouteDefinition("/claims/validate", "ValidateClaim", new WorkStep[]
                {
                    new ComputeStep("edits.apply", 4, 20),
                    new CallStep("dotnet_demo-eligibility-service", "/eligibility/check"),
                    new CallStep("dotnet_demo-benefits-service", "/benefits/resolve"),
                }, FailureRate: 0.04, FailureMessage: "claim failed edit 0342: service date outside coverage"),
            }),

        new("dotnet_demo-eligibility-service", 6007, "Coverage-on-date-of-service determination.", "domain",
            new[]
            {
                new RouteDefinition("/eligibility/check", "CheckEligibility", new WorkStep[]
                {
                    new CacheStep("eligibility", 0.5),
                    new DbStep("SELECT", "coverage_spans", "SELECT * FROM coverage_spans WHERE member_id = $1 AND $2 BETWEEN start_date AND end_date", 5, 25),
                    new CallStep(LegacyAdapterName, "/mainframe/eligibility"),
                }),
            }),

        new("dotnet_demo-benefits-service", 6008, "Benefit plan and limitation resolution.", "domain",
            new[]
            {
                new RouteDefinition("/benefits/resolve", "ResolveBenefits", new WorkStep[]
                {
                    new DbStep("SELECT", "benefit_plans", "SELECT * FROM benefit_plans WHERE plan_id = $1", 4, 20),
                    new ComputeStep("limitations.evaluate", 2, 12),
                    new CallStep("dotnet_demo-pricing-service", "/pricing/calculate"),
                }),
            }),

        new("dotnet_demo-pricing-service", 6009, "Fee schedule pricing and allowed-amount calculation.", "domain",
            new[]
            {
                new RouteDefinition("/pricing/calculate", "CalculatePrice", new WorkStep[]
                {
                    new CacheStep("fee-schedule", 0.75),
                    new DbStep("SELECT", "fee_schedules", "SELECT allowed FROM fee_schedules WHERE hcpcs = $1 AND effective <= $2", 3, 16),
                    new ComputeStep("pricing.compute", 2, 14),
                    new CallStep("dotnet_demo-adjudication-service", "/adjudication/run"),
                }),
            }),

        new("dotnet_demo-adjudication-service", 6010, "Pay/deny decision engine.", "domain",
            new[]
            {
                new RouteDefinition("/adjudication/run", "Adjudicate", new WorkStep[]
                {
                    new ComputeStep("rules.engine", 8, 40),
                    new DbStep("UPDATE", "claims", "UPDATE claims SET status = $1, allowed = $2 WHERE icn = $3", 4, 18),
                    new CallStep("dotnet_demo-payment-service", "/payments/schedule"),
                    new CallStep("dotnet_demo-audit-service", "/audit/record"),
                    // Operational dashboards are refreshed on each decision. Optional, so a
                    // reporting outage degrades the span instead of failing the claim. This
                    // is also what puts all 16 services in a single /claims/submit trace.
                    new CallStep("dotnet_demo-reporting-service", "/reports/build", Optional: true),
                }, FailureRate: 0.05, FailureMessage: "adjudication rule set 'NCCI' returned a hard denial"),
            }),

        // ---- money and downstream effects -----------------------------------
        new("dotnet_demo-payment-service", 6011, "Schedules remittance and payment runs.", "domain",
            new[]
            {
                new RouteDefinition("/payments/schedule", "SchedulePayment", new WorkStep[]
                {
                    new DbStep("INSERT", "payment_lines", "INSERT INTO payment_lines (icn, payee, amount) VALUES ($1,$2,$3)", 4, 22),
                    new CallStep("dotnet_demo-document-service", "/documents/generate"),
                    new CallStep("dotnet_demo-notification-service", "/notify/send", Optional: true),
                    new QueueStep("payments.scheduled"),
                }),
            }),

        new("dotnet_demo-notification-service", 6012, "Member and provider notifications.", "platform",
            new[]
            {
                new RouteDefinition("/notify/send", "SendNotification", new WorkStep[]
                {
                    new ComputeStep("template.render", 2, 10),
                    new QueueStep("notifications.outbound"),
                }, FailureRate: 0.06, FailureMessage: "SMTP relay timed out"),
            }),

        new("dotnet_demo-audit-service", 6013, "Immutable audit trail for every decision.", "platform",
            new[]
            {
                new RouteDefinition("/audit/record", "RecordAudit", new WorkStep[]
                {
                    new DbStep("INSERT", "audit_events", "INSERT INTO audit_events (icn, actor, action) VALUES ($1,$2,$3)", 3, 14),
                }),
                new RouteDefinition("/audit/query", "QueryAudit", new WorkStep[]
                {
                    new DbStep("SELECT", "audit_events", "SELECT * FROM audit_events WHERE icn = $1 ORDER BY ts", 6, 30),
                }),
            }),

        new("dotnet_demo-document-service", 6014, "EOB and remittance advice generation.", "platform",
            new[]
            {
                new RouteDefinition("/documents/generate", "GenerateDocument", new WorkStep[]
                {
                    new ComputeStep("pdf.render", 10, 60),
                    new DbStep("INSERT", "documents", "INSERT INTO documents (icn, kind, uri) VALUES ($1,$2,$3)", 4, 18, System: "s3"),
                }),
            }),

        new("dotnet_demo-reporting-service", 6015, "Operational reporting over claims and audit data.", "analytics",
            new[]
            {
                new RouteDefinition("/reports/build", "BuildReport", new WorkStep[]
                {
                    new DbStep("SELECT", "claims", "SELECT status, count(*) FROM claims GROUP BY status", 20, 90),
                    new CallStep("dotnet_demo-audit-service", "/audit/query"),
                    new ComputeStep("report.aggregate", 10, 45),
                }),
            }),
    };

    private static readonly Dictionary<string, int> PortsByName =
        Services.ToDictionary(s => s.Name, s => s.Port, StringComparer.OrdinalIgnoreCase);

    public static ServiceDefinition? Find(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a service name to its base address, including the external legacy adapter.</summary>
    public static string ResolveBaseAddress(string serviceName)
    {
        if (string.Equals(serviceName, LegacyAdapterName, StringComparison.OrdinalIgnoreCase))
        {
            return $"http://localhost:{LegacyAdapterPort}";
        }

        if (PortsByName.TryGetValue(serviceName, out var port))
        {
            return $"http://localhost:{port}";
        }

        throw new InvalidOperationException($"Unknown service '{serviceName}' in topology.");
    }
}
