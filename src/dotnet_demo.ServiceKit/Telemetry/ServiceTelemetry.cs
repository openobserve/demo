using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Per-process instrumentation primitives. Each service in the platform runs as its own
/// process with its own service name, so the ActivitySource and Meter are created once at
/// startup from that name — which is what makes each one a distinct service in OpenObserve.
/// </summary>
public static class ServiceTelemetry
{
    public static string ServiceName { get; private set; } = "dotnet_demo-unknown-service";

    public static string ServiceVersion { get; private set; } = "1.0.0";

    private static ActivitySource? _source;
    private static Meter? _meter;

    public static ActivitySource Source =>
        _source ?? throw new InvalidOperationException("ServiceTelemetry.Initialize has not been called.");

    public static Meter Meter =>
        _meter ?? throw new InvalidOperationException("ServiceTelemetry.Initialize has not been called.");

    // Platform-wide metrics — identical instrument names across all services, so a single
    // OpenObserve panel can break any of them down by service_name.
    public static Counter<long> RequestsHandled { get; private set; } = null!;
    public static Counter<long> DownstreamCalls { get; private set; } = null!;
    public static Counter<long> Failures { get; private set; } = null!;
    public static Histogram<double> OperationDuration { get; private set; } = null!;
    public static Histogram<double> DependencyDuration { get; private set; } = null!;
    public static UpDownCounter<long> InFlight { get; private set; } = null!;

    public static void Initialize(string serviceName, string serviceVersion)
    {
        if (_source is not null)
        {
            return;
        }

        ServiceName = serviceName;
        ServiceVersion = serviceVersion;

        _source = new ActivitySource(serviceName, serviceVersion);
        _meter = new Meter(serviceName, serviceVersion);

        RequestsHandled = _meter.CreateCounter<long>(
            "dotnet_demo.requests", "{request}", "Requests handled by this service.");
        DownstreamCalls = _meter.CreateCounter<long>(
            "dotnet_demo.downstream.calls", "{call}", "Calls this service made to another service.");
        Failures = _meter.CreateCounter<long>(
            "dotnet_demo.failures", "{failure}", "Failed operations by kind.");
        OperationDuration = _meter.CreateHistogram<double>(
            "dotnet_demo.operation.duration", "ms", "Duration of a business operation.");
        DependencyDuration = _meter.CreateHistogram<double>(
            "dotnet_demo.dependency.duration", "ms", "Duration of a dependency call (db, cache, queue, http).");
        InFlight = _meter.CreateUpDownCounter<long>(
            "dotnet_demo.in_flight", "{request}", "Requests currently in flight.");
    }

    public static void RecordException(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("error", true);
        activity.SetTag("exception.type", ex.GetType().FullName);
        activity.SetTag("exception.message", ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.ToString() },
        }));
    }
}
