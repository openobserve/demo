using System.Diagnostics;
using dotnet_demo.ServiceKit.Telemetry;
using dotnet_demo.ServiceKit.Topology;

namespace dotnet_demo.ServiceKit;

/// <summary>
/// Executes a route's steps, turning each into a span with conventional attributes.
/// <see cref="CallStep"/> goes out over real HTTP, so the W3C traceparent header is
/// injected by the HttpClient instrumentation and the callee's server span joins the
/// same trace — that is what produces the deep cross-service traces.
/// </summary>
public sealed class WorkStepExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WorkStepExecutor> _logger;

    public WorkStepExecutor(IHttpClientFactory httpClientFactory, ILogger<WorkStepExecutor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(RouteDefinition route, HttpContext context, CancellationToken ct)
    {
        foreach (var step in route.Steps)
        {
            switch (step)
            {
                case DbStep db:
                    await RunDbAsync(db, ct);
                    break;
                case CacheStep cache:
                    await RunCacheAsync(cache, ct);
                    break;
                case ComputeStep compute:
                    await RunComputeAsync(compute, ct);
                    break;
                case QueueStep queue:
                    await RunQueueAsync(queue, ct);
                    break;
                case CallStep call:
                    await RunCallAsync(call, context, ct);
                    break;
            }
        }
    }

    private static async Task DelayAsync(int minMs, int maxMs, CancellationToken ct) =>
        await Task.Delay(Random.Shared.Next(minMs, Math.Max(minMs + 1, maxMs)), ct);

    private async Task RunDbAsync(DbStep step, CancellationToken ct)
    {
        using var activity = ServiceTelemetry.Source.StartActivity(step.Name, ActivityKind.Client);
        activity?.SetTag("db.system", step.System);
        activity?.SetTag("db.name", ServiceTelemetry.ServiceName.Replace("dotnet_demo-", string.Empty));
        activity?.SetTag("db.operation", step.Operation);
        activity?.SetTag("db.sql.table", step.Table);
        activity?.SetTag("db.statement", step.Statement);
        activity?.SetTag("server.address", $"{step.Table}-db.dotnet_demo.internal");
        activity?.SetTag("server.port", step.System == "s3" ? 443 : 5432);

        var sw = Stopwatch.StartNew();
        await DelayAsync(step.MinMs, step.MaxMs, ct);
        sw.Stop();

        ServiceTelemetry.DependencyDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("dependency.kind", "db"),
            new KeyValuePair<string, object?>("db.operation", step.Operation));
    }

    private async Task RunCacheAsync(CacheStep step, CancellationToken ct)
    {
        using var activity = ServiceTelemetry.Source.StartActivity(step.Name, ActivityKind.Client);
        activity?.SetTag("db.system", "redis");
        activity?.SetTag("cache.key", step.Key);
        activity?.SetTag("server.address", "cache.dotnet_demo.internal");

        var sw = Stopwatch.StartNew();
        await DelayAsync(step.MinMs, step.MaxMs, ct);

        var hit = Random.Shared.NextDouble() < step.HitRate;
        activity?.SetTag("cache.hit", hit);

        if (!hit)
        {
            // A miss costs a rebuild — visible as a longer span on some traces only.
            activity?.AddEvent(new ActivityEvent("cache.miss"));
            await DelayAsync(5, 25, ct);
        }

        sw.Stop();
        ServiceTelemetry.DependencyDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("dependency.kind", "cache"),
            new KeyValuePair<string, object?>("cache.result", hit ? "hit" : "miss"));
    }

    private static async Task RunComputeAsync(ComputeStep step, CancellationToken ct)
    {
        using var activity = ServiceTelemetry.Source.StartActivity(step.Name, ActivityKind.Internal);
        activity?.SetTag("code.function", step.Label);
        await DelayAsync(step.MinMs, step.MaxMs, ct);
    }

    private async Task RunQueueAsync(QueueStep step, CancellationToken ct)
    {
        using var activity = ServiceTelemetry.Source.StartActivity($"{step.Queue} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", step.Queue);
        activity?.SetTag("messaging.operation", "publish");

        var sw = Stopwatch.StartNew();
        await DelayAsync(step.MinMs, step.MaxMs, ct);
        sw.Stop();

        ServiceTelemetry.DependencyDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("dependency.kind", "queue"),
            new KeyValuePair<string, object?>("messaging.destination.name", step.Queue));

        _logger.LogDebug("Published to {Queue}", step.Queue);
    }

    private async Task RunCallAsync(CallStep step, HttpContext context, CancellationToken ct)
    {
        using var activity = ServiceTelemetry.Source.StartActivity($"call {ShortName(step.TargetService)}", ActivityKind.Internal);
        activity?.SetTag("peer.service", step.TargetService);
        activity?.SetTag("app.dependency.optional", step.Optional);

        var baseAddress = ServiceCatalog.ResolveBaseAddress(step.TargetService);
        var client = _httpClientFactory.CreateClient("downstream");
        var url = $"{baseAddress}{step.Path}";

        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Business correlation id rides alongside the W3C trace context.
            if (context.Items.TryGetValue(TraceContextMiddleware.CorrelationIdHeader, out var correlationId)
                && correlationId is string cid)
            {
                request.Headers.TryAddWithoutValidation(TraceContextMiddleware.CorrelationIdHeader, cid);
            }

            using var response = await client.SendAsync(request, ct);
            sw.Stop();

            ServiceTelemetry.DownstreamCalls.Add(1,
                new KeyValuePair<string, object?>("peer.service", step.TargetService),
                new KeyValuePair<string, object?>("http.response.status_code", (int)response.StatusCode));
            ServiceTelemetry.DependencyDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("dependency.kind", "http"),
                new KeyValuePair<string, object?>("peer.service", step.TargetService));

            if (!response.IsSuccessStatusCode)
            {
                activity?.SetStatus(ActivityStatusCode.Error, $"downstream {(int)response.StatusCode}");

                if (step.Optional)
                {
                    // Degraded, not failed: the trace shows the error without failing the parent.
                    activity?.AddEvent(new ActivityEvent("dependency.degraded"));
                    _logger.LogWarning(
                        "Optional dependency {Target} returned {StatusCode}; continuing",
                        step.TargetService, (int)response.StatusCode);
                    return;
                }

                _logger.LogError(
                    "Dependency {Target} returned {StatusCode} for {Url}",
                    step.TargetService, (int)response.StatusCode, url);

                throw new DownstreamFailureException(step.TargetService, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not DownstreamFailureException && ex is not OperationCanceledException)
        {
            sw.Stop();
            ServiceTelemetry.RecordException(activity, ex);
            ServiceTelemetry.Failures.Add(1,
                new KeyValuePair<string, object?>("kind", "dependency"),
                new KeyValuePair<string, object?>("peer.service", step.TargetService));

            if (step.Optional)
            {
                _logger.LogWarning(ex, "Optional dependency {Target} unreachable; continuing", step.TargetService);
                return;
            }

            _logger.LogError(ex, "Dependency {Target} unreachable at {Url}", step.TargetService, url);
            throw;
        }
    }

    private static string ShortName(string serviceName) => serviceName.Replace("dotnet_demo-", string.Empty);
}

public sealed class DownstreamFailureException : Exception
{
    public DownstreamFailureException(string service, int statusCode)
        : base($"Downstream service '{service}' returned HTTP {statusCode}")
    {
        Service = service;
        StatusCode = statusCode;
    }

    public string Service { get; }

    public int StatusCode { get; }
}
