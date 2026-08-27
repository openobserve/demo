using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Adds the last mile of log↔trace correlation.
///
/// 1. Copies the ambient trace/span id onto the log record as plain attributes
///    (trace_id / span_id). The OTLP exporter already sets them on the record's
///    dedicated fields; duplicating them as attributes means a plain full-text
///    search in OpenObserve for a trace id also hits the logs.
/// 2. Mirrors Warning+ logs onto the active span as span events, so the trace
///    waterfall itself shows what went wrong.
/// </summary>
public sealed class LogEnrichmentProcessor : BaseProcessor<LogRecord>
{
    private readonly OpenObserveOptions _options;

    public LogEnrichmentProcessor(OpenObserveOptions options) => _options = options;

    public override void OnEnd(LogRecord record)
    {
        var activity = Activity.Current;

        var traceId = record.TraceId != default ? record.TraceId
            : activity?.TraceId ?? default;
        var spanId = record.SpanId != default ? record.SpanId
            : activity?.SpanId ?? default;

        var attributes = new List<KeyValuePair<string, object?>>(record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>());

        if (traceId != default)
        {
            attributes.Add(new KeyValuePair<string, object?>("trace_id", traceId.ToHexString()));
        }

        if (spanId != default)
        {
            attributes.Add(new KeyValuePair<string, object?>("span_id", spanId.ToHexString()));
        }

        if (activity is not null)
        {
            attributes.Add(new KeyValuePair<string, object?>("span_name", activity.DisplayName));

            var correlationId = activity.GetTagItem("app.correlation_id") as string;
            if (!string.IsNullOrEmpty(correlationId))
            {
                attributes.Add(new KeyValuePair<string, object?>("correlation_id", correlationId));
            }
        }

        // service_name is written explicitly, not only as a resource attribute.
        // OpenObserve's correlation discovery matches logs to traces on the service_name
        // *field*, and it must also be listed in the stream's distinct_value_fields
        // (see configure-openobserve.sh). Emitting it directly guarantees the field is
        // present in the logs stream schema with exactly the value the traces stream uses.
        attributes.Add(new KeyValuePair<string, object?>("service_name", _options.ServiceName));
        attributes.Add(new KeyValuePair<string, object?>("service_version", _options.ServiceVersion));
        attributes.Add(new KeyValuePair<string, object?>("deployment.environment", _options.DeploymentEnvironment));
        attributes.Add(new KeyValuePair<string, object?>("log.severity", record.LogLevel.ToString()));

        record.Attributes = attributes;

        if (activity is not null && record.LogLevel >= LogLevel.Warning)
        {
            activity.AddEvent(new ActivityEvent("log", tags: new ActivityTagsCollection
            {
                { "log.severity", record.LogLevel.ToString() },
                { "log.category", record.CategoryName },
                { "log.message", record.FormattedMessage ?? record.Body },
                { "exception.type", record.Exception?.GetType().FullName },
                { "exception.message", record.Exception?.Message },
            }));
        }

        base.OnEnd(record);
    }
}
