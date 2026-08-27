namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Everything needed to talk to OpenObserve over OTLP/HTTP.
/// Bound from the "OpenObserve" section of appsettings.json and overridable
/// with environment variables (e.g. OpenObserve__Endpoint=...).
/// </summary>
public sealed class OpenObserveOptions
{
    public const string SectionName = "OpenObserve";

    /// <summary>
    /// Base OTLP endpoint, e.g. https://api.example.dev/api/&lt;org&gt;.
    /// The signal paths (/v1/traces, /v1/metrics, /v1/logs) are appended automatically.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Full value of the Authorization header, e.g. "Basic base64(user:token)".</summary>
    public string Authorization { get; set; } = string.Empty;

    /// <summary>OpenObserve stream that receives the data.</summary>
    public string StreamName { get; set; } = "default";

    public string ServiceName { get; set; } = ServiceTelemetry.ServiceName;

    public string ServiceVersion { get; set; } = ServiceTelemetry.ServiceVersion;

    /// <summary>Value exported as the deployment.environment resource attribute.</summary>
    public string DeploymentEnvironment { get; set; } = "local";

    /// <summary>How often metrics are pushed to OpenObserve.</summary>
    public int MetricExportIntervalMs { get; set; } = 15_000;

    /// <summary>Max delay before a batch of spans/logs is flushed.</summary>
    public int BatchScheduledDelayMs { get; set; } = 5_000;

    /// <summary>Head sampling ratio, 1.0 = keep every trace.</summary>
    public double TraceSampleRatio { get; set; } = 1.0;

    /// <summary>Also write telemetry to stdout — handy when debugging the pipeline.</summary>
    public bool EnableConsoleExporter { get; set; }

    /// <summary>
    /// Log every OTLP export response, not just failures. Failures are always logged.
    /// </summary>
    public bool VerboseExportLogging { get; set; }

    public Uri TracesEndpoint => BuildSignalUri("v1/traces");

    public Uri MetricsEndpoint => BuildSignalUri("v1/metrics");

    public Uri LogsEndpoint => BuildSignalUri("v1/logs");

    private Uri BuildSignalUri(string signalPath)
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Endpoint is not configured. Set it in appsettings.json or via the OpenObserve__Endpoint environment variable.");
        }

        return new Uri($"{Endpoint.TrimEnd('/')}/{signalPath}");
    }
}
