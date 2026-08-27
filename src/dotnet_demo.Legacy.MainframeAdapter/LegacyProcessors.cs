using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace dotnet_demo.Legacy.MainframeAdapter
{
    internal sealed class LegacyOptions
    {
        public string Endpoint { get; set; } = "";
        public string Authorization { get; set; } = "";
        public string StreamName { get; set; } = "default";
        public string DeploymentEnvironment { get; set; } = "local";
        public int Port { get; set; } = 6016;
        public int MetricExportIntervalMs { get; set; } = 15000;
        public bool VerboseExportLogging { get; set; }
    }

    /// <summary>
    /// Same purpose as the modern services' ResourceContextSpanProcessor: OpenObserve's
    /// trace ingestion prefixes every resource attribute except service.name with
    /// "service_", so these are set as span attributes to keep field names identical
    /// across the logs, metrics and traces streams.
    /// </summary>
    internal sealed class LegacyResourceContextProcessor : BaseProcessor<Activity>
    {
        private readonly string _environment;
        private readonly string _host = Environment.MachineName;

        public LegacyResourceContextProcessor(LegacyOptions options)
        {
            _environment = options.DeploymentEnvironment;
        }

        public override void OnStart(Activity activity)
        {
            activity.SetTag("deployment.environment", _environment);
            activity.SetTag("host.name", _host);
            activity.SetTag("service.version", LegacyTelemetry.ServiceVersion);
            activity.SetTag("service.kind", "legacy-adapter");
            base.OnStart(activity);
        }
    }

    /// <summary>Puts trace ids and service identity on every log record, as the modern services do.</summary>
    internal sealed class LegacyLogEnrichmentProcessor : BaseProcessor<LogRecord>
    {
        private readonly LegacyOptions _options;

        public LegacyLogEnrichmentProcessor(LegacyOptions options)
        {
            _options = options;
        }

        public override void OnEnd(LogRecord record)
        {
            var activity = Activity.Current;
            var traceId = record.TraceId != default(ActivityTraceId) ? record.TraceId
                : (activity != null ? activity.TraceId : default(ActivityTraceId));
            var spanId = record.SpanId != default(ActivitySpanId) ? record.SpanId
                : (activity != null ? activity.SpanId : default(ActivitySpanId));

            var attributes = new List<KeyValuePair<string, object>>();
            if (record.Attributes != null)
            {
                attributes.AddRange(record.Attributes);
            }

            if (traceId != default(ActivityTraceId))
            {
                attributes.Add(new KeyValuePair<string, object>("trace_id", traceId.ToHexString()));
            }

            if (spanId != default(ActivitySpanId))
            {
                attributes.Add(new KeyValuePair<string, object>("span_id", spanId.ToHexString()));
            }

            attributes.Add(new KeyValuePair<string, object>("service_name", LegacyTelemetry.ServiceName));
            attributes.Add(new KeyValuePair<string, object>("service_version", LegacyTelemetry.ServiceVersion));
            attributes.Add(new KeyValuePair<string, object>("deployment.environment", _options.DeploymentEnvironment));
            attributes.Add(new KeyValuePair<string, object>("log.severity", record.LogLevel.ToString()));

            record.Attributes = attributes;

            if (activity != null && record.LogLevel >= LogLevel.Warning)
            {
                activity.AddEvent(new ActivityEvent("log", default(DateTimeOffset), new ActivityTagsCollection
                {
                    { "log.severity", record.LogLevel.ToString() },
                    { "log.category", record.CategoryName },
                    { "log.message", record.FormattedMessage ?? record.Body },
                }));
            }

            base.OnEnd(record);
        }
    }
}
