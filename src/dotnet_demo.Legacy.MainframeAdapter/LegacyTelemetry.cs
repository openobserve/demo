using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace dotnet_demo.Legacy.MainframeAdapter
{
    /// <summary>
    /// OpenTelemetry wiring for a .NET Framework service.
    ///
    /// Differences from the ASP.NET Core services, all forced by the platform:
    ///   * No hosting/DI integration — providers are built by hand with Sdk.Create*Builder
    ///     and disposed at shutdown.
    ///   * No AddAspNetCoreInstrumentation: this is a raw HttpListener, so the server span
    ///     and W3C context extraction are written by hand (see LegacyHttpServer).
    ///   * TLS 1.2 must be enabled explicitly; .NET Framework defaults are older than the
    ///     OTLP endpoint accepts.
    ///
    /// The resource, the OTLP endpoint/headers and the span-attribute strategy are
    /// identical to the modern services, so this legacy process correlates with them.
    /// </summary>
    internal static class LegacyTelemetry
    {
        public const string ServiceName = "dotnet_demo-legacy-mainframe-adapter";
        public const string ServiceVersion = "4.7.2";

        public static readonly ActivitySource Source = new ActivitySource(ServiceName, ServiceVersion);
        public static readonly Meter Meter = new Meter(ServiceName, ServiceVersion);

        public static readonly Counter<long> Transactions =
            Meter.CreateCounter<long>("dotnet_demo.requests", "{request}", "Requests handled by this service.");
        public static readonly Counter<long> Failures =
            Meter.CreateCounter<long>("dotnet_demo.failures", "{failure}", "Failed operations by kind.");
        public static readonly Histogram<double> OperationDuration =
            Meter.CreateHistogram<double>("dotnet_demo.operation.duration", "ms", "Duration of a business operation.");
        public static readonly Histogram<double> DependencyDuration =
            Meter.CreateHistogram<double>("dotnet_demo.dependency.duration", "ms", "Duration of a dependency call.");

        private static TracerProvider _tracerProvider;
        private static MeterProvider _meterProvider;
        private static ILoggerFactory _loggerFactory;

        public static ILogger Logger { get; private set; }

        public static void Initialize(LegacyOptions options)
        {
            // .NET Framework negotiates SSLv3/TLS1.0 by default; the collector requires 1.2+.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 32;

            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            Activity.ForceDefaultIdFormat = true;

            var resource = ResourceBuilder.CreateDefault()
                .AddService(ServiceName, serviceVersion: ServiceVersion,
                    serviceInstanceId: Environment.MachineName + "-" + Process.GetCurrentProcess().Id)
                .AddTelemetrySdk()
                .AddAttributes(new Dictionary<string, object>
                {
                    { "deployment.environment", options.DeploymentEnvironment },
                    { "host.name", Environment.MachineName },
                    { "os.description", Environment.OSVersion.ToString() },
                    { "process.runtime.description", RuntimeDescription() },
                    { "service.kind", "legacy-adapter" },
                    { "telemetry.exporter", "otlp-http" },
                });

            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(Source.Name)
                .AddProcessor(new LegacyResourceContextProcessor(options))
                .AddOtlpExporter(otlp => Configure(otlp, options, "v1/traces", "traces"))
                .Build();

            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(Meter.Name)
                .AddOtlpExporter((otlp, reader) =>
                {
                    Configure(otlp, options, "v1/metrics", "metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = options.MetricExportIntervalMs;
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                })
                .Build();

            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddSimpleConsole(c =>
                {
                    c.IncludeScopes = true;
                    c.SingleLine = true;
                    c.TimestampFormat = "HH:mm:ss.fff ";
                });
                builder.AddOpenTelemetry(logging =>
                {
                    logging.SetResourceBuilder(resource);
                    logging.IncludeScopes = true;
                    logging.IncludeFormattedMessage = true;
                    logging.ParseStateValues = true;
                    logging.AddProcessor(new LegacyLogEnrichmentProcessor(options));
                    logging.AddOtlpExporter(otlp => Configure(otlp, options, "v1/logs", "logs"));
                });
            });

            Logger = _loggerFactory.CreateLogger(ServiceName);
        }

        public static void Shutdown()
        {
            // Manual flush: no host lifetime to do it for us.
            _tracerProvider?.ForceFlush(5000);
            _meterProvider?.ForceFlush(5000);
            _tracerProvider?.Dispose();
            _meterProvider?.Dispose();
            _loggerFactory?.Dispose();
        }

        private static void Configure(OtlpExporterOptions otlp, LegacyOptions options, string signalPath, string signal)
        {
            otlp.Endpoint = new Uri(options.Endpoint.TrimEnd('/') + "/" + signalPath);
            otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
            otlp.TimeoutMilliseconds = 15000;
            otlp.HttpClientFactory = () =>
            {
                var client = new HttpClient(new LegacyExportDiagnosticsHandler(signal, options.VerboseExportLogging))
                {
                    Timeout = TimeSpan.FromMilliseconds(15000)
                };
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", options.Authorization);
                client.DefaultRequestHeaders.TryAddWithoutValidation("stream-name", options.StreamName);
                return client;
            };
        }

        private static string RuntimeDescription()
        {
#if NET472
            return ".NET Framework " + Environment.Version;
#else
            return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#endif
        }
    }
}
