using System.Diagnostics;
using dotnet_demo.ServiceKit.Telemetry;
using dotnet_demo.ServiceKit.Topology;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace dotnet_demo.ServiceKit;

/// <summary>
/// Builds and runs one service of the platform from its <see cref="ServiceDefinition"/>.
/// Every service is a real OS process with its own service.name, port and dependencies,
/// so OpenObserve sees a genuine multi-service topology.
/// </summary>
public static class PlatformService
{
    public static async Task RunAsync(ServiceDefinition definition, string[] args)
    {
        // ContentRootPath is pinned to the binary's directory: the launcher starts every
        // service with `dotnet <path>/dotnet_demo.Platform.dll` from the repo root, and the
        // default content root is the *working* directory — which would not contain
        // appsettings.json and would leave the OTLP exporter unconfigured.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseUrls($"http://localhost:{definition.Port}");
        builder.AddOpenObserveTelemetry(definition.Name);

        // CORS for the browser console. Without this the preflight fails and no request
        // reaches the service at all. The allowed headers are the ones the OpenObserve RUM
        // SDK injects: W3C traceparent/tracestate from the 'tracecontext' propagator, and
        // the x-openobserve-* set from the 'openobserve' propagator. The exposed headers
        // are what lets browser JS read back the backend trace id.
        var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"]
                              ?? "http://localhost:5173,http://127.0.0.1:5173")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
            .WithOrigins(allowedOrigins)
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders(
                "Accept",
                "Content-Type",
                "traceparent",
                "tracestate",
                "b3",
                "x-openobserve-trace-id",
                "x-openobserve-parent-id",
                "x-openobserve-origin",
                "x-openobserve-sampling-priority",
                "X-Correlation-Id")
            .WithExposedHeaders(
                TraceContextMiddleware.TraceIdHeader,
                TraceContextMiddleware.SpanIdHeader,
                TraceContextMiddleware.CorrelationIdHeader)
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));

        builder.Services.AddSingleton(definition);
        builder.Services.AddScoped<WorkStepExecutor>();
        builder.Services.AddHttpClient("downstream", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("X-Calling-Service", definition.Name);
        });

        if (definition.IsEntryPoint)
        {
            builder.Services.AddHostedService<PlatformTrafficDriver>();
        }

        // Scopes on: TraceContextMiddleware pushes trace_id into every log scope, so
        // `grep <trace id> logs/*.log` shows every service that took part in a trace.
        builder.Logging.AddSimpleConsole(c =>
        {
            c.IncludeScopes = true;
            c.SingleLine = true;
            c.TimestampFormat = "HH:mm:ss.fff ";
        });

        var app = builder.Build();

        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerFeature>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(definition.Name);
            var activity = Activity.Current;

            if (feature?.Error is { } error)
            {
                ServiceTelemetry.RecordException(activity, error);
                logger.LogError(error, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Internal Server Error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = feature?.Error.Message,
                Instance = context.Request.Path,
                Extensions =
                {
                    ["service"] = definition.Name,
                    ["traceId"] = activity?.TraceId.ToHexString(),
                },
            });
        }));

        app.UseCors();
        app.UseTraceContext();

        foreach (var route in definition.Routes)
        {
            MapRoute(app, definition, route);
        }

        app.MapGet("/", () => Results.Ok(new
        {
            service = definition.Name,
            tier = definition.Tier,
            description = definition.Description,
            port = definition.Port,
            // Reported so the running runtime is verifiable over HTTP, not just with ps.
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            targetFramework = "net6.0",
            routes = definition.Routes.Select(r => r.Path),
            dependencies = definition.Routes
                .SelectMany(r => r.Steps.OfType<CallStep>())
                .Select(c => c.TargetService)
                .Distinct(),
            traceId = Activity.Current?.TraceId.ToHexString(),
        }));

        app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = definition.Name }));

        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        startupLogger.LogInformation(
            "{Service} ({Tier}) listening on http://localhost:{Port} — {RouteCount} route(s)",
            definition.Name, definition.Tier, definition.Port, definition.Routes.Count);

        await app.RunAsync();
    }

    private static void MapRoute(WebApplication app, ServiceDefinition definition, RouteDefinition route)
    {
        app.MapGet(route.Path, async (HttpContext context, WorkStepExecutor executor, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger(definition.Name);

            using var activity = ServiceTelemetry.Source.StartActivity(route.Operation, ActivityKind.Internal);
            activity?.SetTag("app.operation", route.Operation);
            activity?.SetTag("app.service.tier", definition.Tier);

            var stopwatch = Stopwatch.StartNew();
            ServiceTelemetry.InFlight.Add(1);

            try
            {
                logger.LogInformation("{Operation} started", route.Operation);

                await executor.ExecuteAsync(route, context, ct);

                // Injected business failure, distinct from a dependency failure.
                if (route.FailureRate > 0 && Random.Shared.NextDouble() < route.FailureRate)
                {
                    throw new InvalidOperationException(route.FailureMessage ?? $"{route.Operation} failed");
                }

                stopwatch.Stop();
                ServiceTelemetry.RequestsHandled.Add(1,
                    new KeyValuePair<string, object?>("operation", route.Operation),
                    new KeyValuePair<string, object?>("outcome", "success"));
                ServiceTelemetry.OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", route.Operation),
                    new KeyValuePair<string, object?>("outcome", "success"));

                activity?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation("{Operation} completed in {ElapsedMs:F1}ms",
                    route.Operation, stopwatch.Elapsed.TotalMilliseconds);

                return Results.Ok(new
                {
                    service = definition.Name,
                    operation = route.Operation,
                    durationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                    traceId = Activity.Current?.TraceId.ToHexString(),
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ServiceTelemetry.RequestsHandled.Add(1,
                    new KeyValuePair<string, object?>("operation", route.Operation),
                    new KeyValuePair<string, object?>("outcome", "failure"));
                ServiceTelemetry.OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", route.Operation),
                    new KeyValuePair<string, object?>("outcome", "failure"));
                ServiceTelemetry.Failures.Add(1,
                    new KeyValuePair<string, object?>("kind", ex is DownstreamFailureException ? "dependency" : "business"),
                    new KeyValuePair<string, object?>("operation", route.Operation));

                ServiceTelemetry.RecordException(activity, ex);
                logger.LogError(ex, "{Operation} failed after {ElapsedMs:F1}ms",
                    route.Operation, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
            finally
            {
                ServiceTelemetry.InFlight.Add(-1);
            }
        });
    }
}
