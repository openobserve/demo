using System.Diagnostics;
using OpenTelemetry;

namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Copies the identity attributes onto every span as *span* attributes, in addition to
/// carrying them on the resource.
///
/// Why this is necessary: OpenObserve's three ingestion paths treat resource attributes
/// differently (verified in openobserve v0.14.7 source):
///
///   logs    src/service/logs/otlp.rs   service_att_map.insert(res_attr.key, ...)
///                                      -> deployment_environment, host_name
///   metrics src/service/metrics/otlp.rs rec[format_label_name(item.key)] = ...
///                                      -> deployment_environment, host_name
///   traces  src/service/traces/mod.rs  format!("{}_{}", SERVICE, res_attr.key)
///                                      -> service_deployment_environment, service_host_name
///
/// Only the traces path prefixes, and it prefixes everything except service.name. That
/// leaves trace records with field names that do not match the log and metric records,
/// which breaks OpenObserve's workload detection for log<->trace correlation.
///
/// Span attributes are stored with their key unchanged (only _timestamp/duration/
/// start_time/end_time are renamed), so setting them here yields the unprefixed
/// deployment_environment / host_name / service_version fields on every span — matching
/// what the logs and metrics streams already have.
/// </summary>
public sealed class ResourceContextSpanProcessor : BaseProcessor<Activity>
{
    private readonly string _deploymentEnvironment;
    private readonly string _serviceVersion;
    private readonly string _hostName = Environment.MachineName;
    private readonly string _serviceInstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";

    public ResourceContextSpanProcessor(OpenObserveOptions options)
    {
        _deploymentEnvironment = options.DeploymentEnvironment;
        _serviceVersion = options.ServiceVersion;
    }

    public override void OnStart(Activity activity)
    {
        // service.name is deliberately not set here: the traces path already stores it
        // unprefixed as service_name, so adding it again would only duplicate the column.
        activity.SetTag("deployment.environment", _deploymentEnvironment);
        activity.SetTag("host.name", _hostName);
        activity.SetTag("service.version", _serviceVersion);
        activity.SetTag("service.instance.id", _serviceInstanceId);

        base.OnStart(activity);
    }
}
