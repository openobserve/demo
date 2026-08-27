using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Wires traces, metrics and logs into OpenObserve over OTLP/HTTP.
///
/// Correlation model:
///   * All three signals share one <see cref="Resource"/> (service.name / version /
///     instance id / environment), so OpenObserve can pivot between them by service.
///   * The OpenTelemetry logging provider stamps every log record with the trace_id and
///     span_id of the ambient <see cref="Activity"/>, which is what makes
///     "jump from a span to its logs" work without any manual plumbing.
///   * Metrics carry trace-based exemplars, so a latency bucket points back at a trace.
/// </summary>
public static class OpenObserveTelemetryExtensions
{
    /// <param name="serviceName">
    /// Identity of this process. Every service in the platform runs the same kit, so the
    /// name is supplied per process rather than read from shared config — it becomes
    /// service.name on all three signals and the ActivitySource/Meter name.
    /// </param>
    public static WebApplicationBuilder AddOpenObserveTelemetry(
        this WebApplicationBuilder builder,
        string serviceName,
        string? serviceVersion = null)
    {
        var version = serviceVersion
                      ?? builder.Configuration[$"{OpenObserveOptions.SectionName}:ServiceVersion"]
                      ?? "1.0.0";

        ServiceTelemetry.Initialize(serviceName, version);

        builder.Services
            .AddOptions<OpenObserveOptions>()
            .Bind(builder.Configuration.GetSection(OpenObserveOptions.SectionName))
            .PostConfigure(o =>
            {
                o.ServiceName = serviceName;
                o.ServiceVersion = version;
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint), "OpenObserve:Endpoint must be configured.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Authorization), "OpenObserve:Authorization must be configured.");

        var options = builder.Configuration
            .GetSection(OpenObserveOptions.SectionName)
            .Get<OpenObserveOptions>() ?? new OpenObserveOptions();

        options.ServiceName = serviceName;
        options.ServiceVersion = version;

        // W3C trace context on the wire, so trace ids survive process hops.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var resourceBuilder = BuildResource(options);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => ConfigureTracing(tracing, options, resourceBuilder))
            .WithMetrics(metrics => ConfigureMetrics(metrics, options, resourceBuilder));

        ConfigureLogging(builder, options, resourceBuilder);

        return builder;
    }

    // -------------------------------------------------------------------------
    // Resource: identical for traces, metrics and logs. This is the join key.
    // -------------------------------------------------------------------------
    private static ResourceBuilder BuildResource(OpenObserveOptions options) =>
        ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: options.ServiceName,
                serviceVersion: options.ServiceVersion,
                serviceInstanceId: $"{Environment.MachineName}-{Environment.ProcessId}")
            .AddTelemetrySdk()
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = options.DeploymentEnvironment,
                ["host.name"] = Environment.MachineName,
                ["os.type"] = Environment.OSVersion.Platform.ToString(),
                ["os.description"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["process.runtime.description"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["process.pid"] = Environment.ProcessId,
                ["telemetry.exporter"] = "otlp-http",
            });

    // -------------------------------------------------------------------------
    // Traces
    // -------------------------------------------------------------------------
    private static void ConfigureTracing(TracerProviderBuilder tracing, OpenObserveOptions options, ResourceBuilder resource)
    {
        tracing
            .SetResourceBuilder(resource)
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)))
            // Must run before the exporter's batch processor so the attributes are on the
            // span when it is exported. See the class docs: OpenObserve prefixes resource
            // attributes with "service_" on the traces path only, so these are set as span
            // attributes to keep field names consistent with the logs/metrics streams.
            .AddProcessor(new ResourceContextSpanProcessor(options))
            .AddSource(ServiceTelemetry.Source.Name)
            .AddAspNetCoreInstrumentation(o =>
            {
                o.RecordException = true;
                // Health/metrics probes would otherwise drown the trace stream.
                o.Filter = ctx =>
                {
                    var path = ctx.Request.Path.Value ?? string.Empty;
                    return !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                           && !path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                           && !path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase);
                };
                o.EnrichWithHttpRequest = (activity, request) =>
                {
                    activity.SetTag("http.request.host", request.Host.Value);
                    activity.SetTag("http.request.content_length", request.ContentLength);
                    activity.SetTag("client.address", request.HttpContext.Connection.RemoteIpAddress?.ToString());
                    if (request.Headers.TryGetValue("X-Correlation-Id", out var correlationId))
                    {
                        activity.SetTag("app.correlation_id", correlationId.ToString());
                    }
                };
                o.EnrichWithHttpResponse = (activity, response) =>
                {
                    activity.SetTag("http.response.content_length", response.ContentLength);
                };
                o.EnrichWithException = (activity, ex) => activity.SetTag("exception.source", ex.Source);
            })
            .AddHttpClientInstrumentation(o =>
            {
                o.RecordException = true;
                o.EnrichWithHttpRequestMessage = (activity, request) =>
                    activity.SetTag("http.request.uri", request.RequestUri?.ToString());
                o.EnrichWithHttpResponseMessage = (activity, response) =>
                    activity.SetTag("http.response.status_code", (int)response.StatusCode);
            })
            .AddOtlpExporter(otlp => ConfigureOtlp(otlp, options, options.TracesEndpoint, "traces", options.BatchScheduledDelayMs));

        if (options.EnableConsoleExporter)
        {
            tracing.AddConsoleExporter();
        }
    }

    // -------------------------------------------------------------------------
    // Metrics
    // -------------------------------------------------------------------------
    private static void ConfigureMetrics(MeterProviderBuilder metrics, OpenObserveOptions options, ResourceBuilder resource)
    {
        metrics
            .SetResourceBuilder(resource)
            // Trace-based exemplars: each recorded measurement can carry the trace_id it
            // came from, so a metric spike links straight into a trace.
            .SetExemplarFilter(ExemplarFilterType.TraceBased)
            .AddMeter(ServiceTelemetry.Meter.Name)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddView(
                instrumentName: "dotnet_demo.operation.duration",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = new double[] { 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000 },
                })
            .AddView(
                instrumentName: "dotnet_demo.dependency.duration",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = new double[] { 1, 5, 10, 25, 50, 100, 250, 500, 1000 },
                })
            .AddOtlpExporter((otlp, reader) =>
            {
                ConfigureOtlp(otlp, options, options.MetricsEndpoint, "metrics");
                otlp.TimeoutMilliseconds = 30_000;
                // Jittered per process: 16 services exporting on the same cadence hit the
                // ingest endpoint simultaneously. On a brand-new org that thundering herd
                // races to create the same metric streams and OpenObserve rejects the
                // colliding batches with ArrowJsonEncodeError; afterwards it just makes
                // load spiky. A random offset decorrelates them.
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    options.MetricExportIntervalMs + Random.Shared.Next(0, 5_000);
                // Cumulative is what OpenObserve/Prometheus-style backends expect.
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
            });

        if (options.EnableConsoleExporter)
        {
            metrics.AddConsoleExporter();
        }
    }

    // -------------------------------------------------------------------------
    // Logs — the piece that carries trace_id/span_id into OpenObserve.
    // -------------------------------------------------------------------------
    private static void ConfigureLogging(WebApplicationBuilder builder, OpenObserveOptions options, ResourceBuilder resource)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);

            // IncludeScopes surfaces the enrichment scopes pushed by TraceContextMiddleware
            // (order id, correlation id, ...) as structured log attributes.
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;
            logging.ParseStateValues = true;

            logging.AddProcessor(new LogEnrichmentProcessor(options));

            logging.AddOtlpExporter(otlp =>
                ConfigureOtlp(otlp, options, options.LogsEndpoint, "logs", options.BatchScheduledDelayMs));

            if (options.EnableConsoleExporter)
            {
                logging.AddConsoleExporter();
            }
        });

        // Console logs show the trace id too, so a local terminal line can be pasted
        // into OpenObserve search and find the exact trace.
        builder.Logging.AddSimpleConsole(c =>
        {
            c.IncludeScopes = true;
            c.SingleLine = true;
            c.TimestampFormat = "HH:mm:ss.fff ";
        });
    }

    // -------------------------------------------------------------------------
    // Shared OTLP wiring
    // -------------------------------------------------------------------------
    private static void ConfigureOtlp(
        OtlpExporterOptions otlp,
        OpenObserveOptions options,
        Uri endpoint,
        string signal,
        int? batchDelayMs = null)
    {
        otlp.Endpoint = endpoint;
        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
        otlp.TimeoutMilliseconds = 15_000;

        // Headers are attached through the HttpClient rather than OtlpExporterOptions.Headers:
        // the Basic auth value is base64 and contains '=' padding, which the OTLP header
        // string parser would mangle.
        otlp.HttpClientFactory = () =>
        {
            var client = new HttpClient(new OtlpExportDiagnosticsHandler(signal, options.VerboseExportLogging))
            {
                Timeout = TimeSpan.FromMilliseconds(otlp.TimeoutMilliseconds),
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", options.Authorization);
            client.DefaultRequestHeaders.TryAddWithoutValidation("stream-name", options.StreamName);
            return client;
        };

        if (batchDelayMs is { } delay)
        {
            otlp.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
            {
                ScheduledDelayMilliseconds = delay,
                MaxQueueSize = 4096,
                MaxExportBatchSize = 512,
                ExporterTimeoutMilliseconds = 30_000,
            };
        }
    }
}
