// =============================================================================
// OpenTelemetry -> OpenObserve Cloud : ASP.NET (classic, .NET Framework 4.6.2+)
// Traces only. Drop-in replacement for Global.asax.cs.
//
// Fixes applied over the previous version:
//   1. Sampler defaults to always_on, not parentbased_always_on. A parent-based
//      sampler honours an upstream traceparent; any proxy/CDN/RUM that injects
//      "-00" silently drops every span while everything else looks healthy.
//      Switch to parentbased_always_on only AFTER traces are confirmed flowing.
//   2. log4net correlation properties are set BEFORE Response.Headers, and the
//      header write is isolated in its own try/catch. Response.Headers throws
//      PlatformNotSupportedException under IIS classic mode, which previously
//      skipped the correlation properties and silently broke trace<->log linking.
//   3. Authorization can be supplied as a plain key (OTEL_OPENOBSERVE_AUTHORIZATION)
//      instead of packed inside OTEL_EXPORTER_OTLP_HEADERS. Removes an entire
//      class of parse bugs. The HEADERS form still works as a fallback.
//   4. Endpoint is normalised: give it the base org URL or the full /v1/traces
//      URL, either is accepted. No more "must end in /v1/traces" hard failure.
//   5. Explicit batch processor settings so export timing is predictable, and a
//      guard against double initialisation on app-domain churn.
//   6. Startup diagnostics report the resolved endpoint and a MASKED credential,
//      so the log proves what was actually loaded without leaking the token.
//
// -----------------------------------------------------------------------------
// REQUIRED NUGET PACKAGES
// -----------------------------------------------------------------------------
// Keep all OpenTelemetry.* packages on the SAME core version. The ASP.NET
// (classic) instrumentation only ships as a -beta; use the beta that matches
// your core version. Verify current versions on nuget.org before installing.
//
//   OpenTelemetry
//   OpenTelemetry.Exporter.OpenTelemetryProtocol
//   OpenTelemetry.Instrumentation.AspNet                     (-beta)
//   OpenTelemetry.Instrumentation.AspNet.TelemetryHttpModule (-beta)
//   OpenTelemetry.Instrumentation.Http
//   System.Diagnostics.DiagnosticSource
//
// Do NOT install OpenTelemetry.AutoInstrumentation. This file is manual
// instrumentation; adding auto-instrumentation on top produces duplicate spans.
//
// -----------------------------------------------------------------------------
// Web.config  -->  <configuration><appSettings>
// -----------------------------------------------------------------------------
// <!-- Contains an ingestion credential. Do not commit to source control. -->
// <add key="OTEL_SERVICE_NAME"                    value="SSCNE-WEB" />
// <add key="OTEL_RESOURCE_ATTRIBUTES"             value="service.namespace=SSCNE,deployment.environment.name=test,service.version=1.0.0" />
// <add key="OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"   value="https://api.openobserve.ai/api/YOUR_ORG_ID/v1/traces" />
// <add key="OTEL_OPENOBSERVE_AUTHORIZATION"       value="Basic PASTE_INGESTION_TOKEN_HERE" />
// <add key="OTEL_STREAM_NAME"                     value="dotnet" />
// <add key="OTEL_TRACES_SAMPLER"                  value="always_on" />
// <add key="OTEL_DIAGNOSTIC_MODE"                 value="true" />
// <add key="OTEL_STARTUP_TEST"                    value="true" />
//
// YOUR_ORG_ID and the token both come from the OpenObserve UI:
//   Ingestion -> Custom -> OpenTelemetry
// The Authorization value is shown there already Base64-encoded; copy it whole,
// including the "Basic " prefix. Never commit the real value to source control.
//
// -----------------------------------------------------------------------------
// Web.config  -->  <configuration><system.webServer><modules>
// -----------------------------------------------------------------------------
// <add name="TelemetryHttpModule"
//      type="OpenTelemetry.Instrumentation.AspNet.TelemetryHttpModule, OpenTelemetry.Instrumentation.AspNet.TelemetryHttpModule"
//      preCondition="integratedMode,managedHandler" />
//
// The type name and the assembly name are identical. That looks like a copy
// paste error but it is correct. The app pool must be in Integrated mode.
//
// -----------------------------------------------------------------------------
// OPENOBSERVE CLOUD ENDPOINTS
// -----------------------------------------------------------------------------
// Base:     https://api.openobserve.ai/api/{ORG_ID}
//   traces  {base}/v1/traces      <-- wired below
//   metrics {base}/v1/metrics
//   logs    {base}/v1/logs
//
// All three are OTLP over HTTP/protobuf. The default OTLP protocol in the .NET
// SDK is gRPC, which fails against these URLs, so the protocol is pinned to
// HttpProtobuf explicitly below.
//
// Authorization is HTTP Basic:
//   Basic base64("<login-email>:<ingestion-token>")
// Take the value straight from the OpenObserve UI under
// Ingestion -> Custom -> OpenTelemetry (it is pre-encoded there).
//
// -----------------------------------------------------------------------------
// HOW TO VERIFY (in this order)
// -----------------------------------------------------------------------------
// 1. Set OTEL_STARTUP_TEST=true and OTEL_DIAGNOSTIC_MODE=true, recycle the pool.
// 2. In the log4net log, look for "OTEL INICIO OK".
//      absent  -> look for "ERROR AL INICIAR"; config is wrong, nothing started.
// 3. Look for "OTEL STARTUP TEST FORCEFLUSH => Resultado=True".
//      This proves exporter + auth + TLS + network without any HTTP traffic.
// 4. Look for "OTLP EXPORT FAILED":
//      401/403 -> Authorization wrong.        404 -> endpoint/org id wrong.
//      "OTLP EXPORT EXCEPTION" -> TLS or network/proxy.
// 5. Hit a page. If no request spans appear, look for
//    "Activity.Current es NULL" -> TelemetryHttpModule is not loading.
// 6. Once traces are confirmed: set OTEL_DIAGNOSTIC_MODE=false (it forces
//    synchronous per-span export and will hurt production throughput).
//
// IMPORTANTE: el namespace DEBE coincidir con el Inherits de Global.asax.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SSCNE
{
    public class Global : HttpApplication
    {
        private const string ManualActivitySourceName = "SSCNE-WEB.Manual";
        private const string StopwatchItemKey = "OTEL_REQUEST_STOPWATCH";
        private const string TraceIdItemKey = "OTEL_TRACE_ID";
        private const string SpanIdItemKey = "OTEL_SPAN_ID";
        private const string CorrelationIdItemKey = "OTEL_CORRELATION_ID";

        private const string TracesPath = "v1/traces";

        // Stream de OpenObserve donde aterrizan las trazas. Se envia como header
        // "stream-name"; si no se manda, OpenObserve las deja en "default".
        private const string DefaultStreamName = "dotnet";

        // Estatico: IIS puede crear varias instancias de HttpApplication.
        private static TracerProvider _tracerProvider;
        private static readonly ActivitySource ManualActivitySource =
            new ActivitySource(ManualActivitySourceName, "1.0.0");

        // Evita construir dos TracerProvider si Application_Start corre dos veces.
        private static int _initialized;

        private static bool _diagnosticMode;
        private static string _serviceName = "SSCNE-WEB";
        private static string _deploymentEnvironment = "test";

        // ---------------------------------------------------------------------
        // Application_Start
        // ---------------------------------------------------------------------
        protected void Application_Start(object sender, EventArgs e)
        {
            // CONFIGURACION EXISTENTE DE LOG4NET - NO SE ELIMINA.
            log4net.Config.XmlConfigurator.Configure(new FileInfo(Server.MapPath("~/Web.config")));

            log4net.ILog log = log4net.LogManager.GetLogger(typeof(Global));

            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            {
                log.Warn("OTEL => Application_Start reentrante, se conserva el TracerProvider existente.");
                return;
            }

            try
            {
                InitializeOpenTelemetry(log);
            }
            catch (Exception ex)
            {
                // Una falla de observabilidad no debe tumbar la aplicacion.
                log.Error("ERROR AL INICIAR OPEN TELEMETRY / OPENOBSERVE => " + ex);
            }
        }

        private static void InitializeOpenTelemetry(log4net.ILog log)
        {
            // .NET Framework negocia protocolos antiguos si no se fuerza TLS 1.2.
            // Sin esto el handshake contra api.openobserve.ai falla en silencio.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.Expect100Continue = false;

            // IDs W3C para poder propagar traceparent entre sistemas.
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            Activity.ForceDefaultIdFormat = true;

            string serviceName = AppSetting("OTEL_SERVICE_NAME");
            string resourceAttributes = AppSetting("OTEL_RESOURCE_ATTRIBUTES");
            string rawEndpoint = AppSetting("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");
            string samplerName = AppSetting("OTEL_TRACES_SAMPLER");
            string explicitAuth = AppSetting("OTEL_OPENOBSERVE_AUTHORIZATION");
            string otlpHeaders = AppSetting("OTEL_EXPORTER_OTLP_HEADERS");
            string streamName = AppSetting("OTEL_STREAM_NAME");

            _serviceName = string.IsNullOrEmpty(serviceName) ? "SSCNE-WEB" : serviceName.Trim();
            _diagnosticMode = IsTrue(AppSetting("OTEL_DIAGNOSTIC_MODE"));

            if (string.IsNullOrEmpty(rawEndpoint))
            {
                throw new ConfigurationErrorsException(
                    "Falta OTEL_EXPORTER_OTLP_TRACES_ENDPOINT en Web.config. " +
                    "Esperado: https://api.openobserve.ai/api/{ORG_ID}/v1/traces");
            }

            Uri endpoint = BuildTracesEndpoint(rawEndpoint);

            // Authorization: se acepta la clave directa (preferida) o el formato
            // OTEL_EXPORTER_OTLP_HEADERS. La clave directa evita cualquier parseo.
            Dictionary<string, string> headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(otlpHeaders))
            {
                headers = ParseHeaders(otlpHeaders);
            }

            if (!string.IsNullOrEmpty(explicitAuth))
            {
                headers["Authorization"] = explicitAuth.Trim();
            }

            if (!headers.ContainsKey("Authorization"))
            {
                throw new ConfigurationErrorsException(
                    "Falta la credencial de ingestion. Defina OTEL_OPENOBSERVE_AUTHORIZATION " +
                    "con el valor 'Basic <base64>' o incluya Authorization en OTEL_EXPORTER_OTLP_HEADERS.");
            }

            if (string.IsNullOrEmpty(streamName))
            {
                streamName = DefaultStreamName;
            }

            if (!headers.ContainsKey("stream-name"))
            {
                headers["stream-name"] = streamName;
            }

            List<KeyValuePair<string, object>> resourceAttrs =
                new List<KeyValuePair<string, object>>(ParseResourceAttributes(resourceAttributes));

            string serviceVersion = "1.0.0";
            for (int i = 0; i < resourceAttrs.Count; i++)
            {
                string key = resourceAttrs[i].Key;

                if (string.Equals(key, "deployment.environment", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "deployment.environment.name", StringComparison.OrdinalIgnoreCase))
                {
                    _deploymentEnvironment = Convert.ToString(resourceAttrs[i].Value);
                }
                else if (string.Equals(key, "service.version", StringComparison.OrdinalIgnoreCase))
                {
                    serviceVersion = Convert.ToString(resourceAttrs[i].Value);
                }
            }

            Sampler sampler = BuildSampler(samplerName);

            ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(
                    serviceName: _serviceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName + "-" + Process.GetCurrentProcess().Id)
                .AddAttributes(resourceAttrs)
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("host.name", Environment.MachineName),
                    new KeyValuePair<string, object>("process.pid", Process.GetCurrentProcess().Id),
                    new KeyValuePair<string, object>("process.runtime.description",
                        ".NET Framework " + Environment.Version),
                    new KeyValuePair<string, object>("telemetry.exporter", "otlp-http")
                });

            bool diagnosticMode = _diagnosticMode;

            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .SetSampler(sampler)

                // Necesario para el span de arranque y cualquier span manual.
                .AddSource(ManualActivitySourceName)

                // Solicitudes entrantes de ASP.NET clasico (requiere TelemetryHttpModule).
                .AddAspNetInstrumentation(options =>
                {
                    options.RecordException = true;
                })

                // HttpClient/HttpWebRequest salientes + propagacion de traceparent.
                // El exportador OTLP se auto-excluye mediante SuppressInstrumentationScope,
                // por lo que esto no genera un bucle de telemetria sobre si misma.
                .AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = true;
                })

                .AddOtlpExporter(options =>
                {
                    // Fijar el endpoint por codigo desactiva el auto-append de la ruta
                    // de senal, por eso la URL ya incluye /v1/traces.
                    options.Endpoint = endpoint;

                    // El default del SDK es gRPC y falla contra /v1/traces.
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.TimeoutMilliseconds = 15000;

                    if (diagnosticMode)
                    {
                        // Exporta cada span al terminar: resultado inmediato en el log.
                        // NO dejar activo en produccion, bloquea el hilo de la solicitud.
                        options.ExportProcessorType = ExportProcessorType.Simple;
                    }
                    else
                    {
                        options.ExportProcessorType = ExportProcessorType.Batch;
                        options.BatchExportProcessorOptions =
                            new BatchExportProcessorOptions<Activity>
                            {
                                ScheduledDelayMilliseconds = 5000,
                                MaxQueueSize = 4096,
                                MaxExportBatchSize = 512,
                                ExporterTimeoutMilliseconds = 30000
                            };
                    }

                    // Los headers NO se pasan por options.Headers: el parser del SDK
                    // maltrata el padding '=' del Base64 de Basic auth y produce 401.
                    options.HttpClientFactory = delegate
                    {
                        HttpClient client = new HttpClient(
                            new OtlpDiagnosticsHandler(diagnosticMode));

                        client.Timeout = TimeSpan.FromMilliseconds(15000);

                        foreach (KeyValuePair<string, string> header in headers)
                        {
                            client.DefaultRequestHeaders.TryAddWithoutValidation(
                                header.Key, header.Value);
                        }

                        return client;
                    };
                })
                .Build();

            log.Info(
                "OTEL INICIO OK => Clase=" + typeof(Global).FullName +
                ", Servicio=" + _serviceName +
                ", Version=" + serviceVersion +
                ", Ambiente=" + _deploymentEnvironment +
                ", Endpoint=" + endpoint +
                ", Stream=" + streamName +
                ", Sampler=" + (string.IsNullOrEmpty(samplerName) ? "always_on (default)" : samplerName) +
                ", Auth=" + Mask(headers["Authorization"]) +
                ", DiagnosticMode=" + _diagnosticMode);

            if (IsTrue(AppSetting("OTEL_STARTUP_TEST")))
            {
                RunStartupTest(log);
            }
        }

        /// <summary>
        /// Span controlado: comprueba exporter, credencial, TLS y red sin depender
        /// de que IIS reciba trafico ni de que el modulo HTTP este cargado.
        /// </summary>
        private static void RunStartupTest(log4net.ILog log)
        {
            using (Activity startupActivity = ManualActivitySource.StartActivity(
                "SSCNE.OTEL.STARTUP.TEST", ActivityKind.Internal))
            {
                if (startupActivity == null)
                {
                    log.Error(
                        "OTEL STARTUP TEST ERROR => StartActivity devolvio NULL. " +
                        "El sampler esta en always_off o falta AddSource(" +
                        ManualActivitySourceName + ").");
                }
                else
                {
                    startupActivity.SetTag("otel.test", true);
                    startupActivity.SetTag("app.name", "SSCNE");
                    startupActivity.SetTag("service.name", _serviceName);
                    startupActivity.SetTag("deployment.environment", _deploymentEnvironment);

                    log.Info(
                        "OTEL STARTUP TEST CREADO => TraceId=" +
                        startupActivity.TraceId.ToHexString() +
                        ", SpanId=" + startupActivity.SpanId.ToHexString());
                }
            }

            bool flushed = _tracerProvider.ForceFlush(10000);

            log.Info("OTEL STARTUP TEST FORCEFLUSH => Resultado=" + flushed);

            if (!flushed)
            {
                log.Error(
                    "OTEL STARTUP TEST => ForceFlush devolvio False. La exportacion no " +
                    "completo en 10s. Revise 'OTLP EXPORT FAILED' / 'OTLP EXPORT EXCEPTION'.");
            }
        }

        // ---------------------------------------------------------------------
        // Ciclo de vida de la solicitud
        // ---------------------------------------------------------------------
        protected void Application_BeginRequest(object sender, EventArgs e)
        {
            log4net.ILog log = log4net.LogManager.GetLogger(typeof(Global));

            try
            {
                Context.Items[StopwatchItemKey] = Stopwatch.StartNew();

                string correlationId = Request.Headers["X-Correlation-Id"];
                if (string.IsNullOrEmpty(correlationId))
                {
                    correlationId = Guid.NewGuid().ToString("N");
                }

                Context.Items[CorrelationIdItemKey] = correlationId;
                log4net.LogicalThreadContext.Properties["correlation_id"] = correlationId;

                Activity activity = Activity.Current;
                if (activity == null)
                {
                    // Senal mas util para detectar que TelemetryHttpModule no creo el span.
                    log.Warn(
                        "OTEL WARNING => Application_BeginRequest ejecuto pero Activity.Current es NULL. " +
                        "Revise TelemetryHttpModule en <system.webServer><modules>, la DLL de " +
                        "OpenTelemetry.Instrumentation.AspNet, el Application Pool en modo Integrated " +
                        "y el Inherits de Global.asax.");
                    return;
                }

                string traceId = activity.TraceId.ToHexString();
                string spanId = activity.SpanId.ToHexString();

                Context.Items[TraceIdItemKey] = traceId;
                Context.Items[SpanIdItemKey] = spanId;

                activity.SetTag("app.correlation_id", correlationId);
                activity.SetTag("app.service", _serviceName);
                activity.SetTag("deployment.environment", _deploymentEnvironment);

                // ORDEN IMPORTANTE: la correlacion de log4net se fija ANTES de tocar
                // Response.Headers, que lanza PlatformNotSupportedException en modo
                // IIS clasico y antes se llevaba por delante estas tres lineas.
                log4net.LogicalThreadContext.Properties["trace_id"] = traceId;
                log4net.LogicalThreadContext.Properties["span_id"] = spanId;

                try
                {
                    Response.Headers["X-Trace-Id"] = traceId;
                    Response.Headers["X-Span-Id"] = spanId;
                    Response.Headers["X-Correlation-Id"] = correlationId;
                }
                catch (PlatformNotSupportedException)
                {
                    // IIS en modo clasico: no se pueden escribir headers asi. No es fatal.
                }

                if (_diagnosticMode)
                {
                    log.Info(
                        "OTEL REQUEST START => " + Request.HttpMethod + " " + Request.Path +
                        ", TraceId=" + traceId +
                        ", SpanId=" + spanId +
                        ", CorrelationId=" + correlationId);
                }
            }
            catch (Exception ex)
            {
                log.Error("OTEL BeginRequest ERROR => " + ex);
            }
        }

        protected void Application_EndRequest(object sender, EventArgs e)
        {
            log4net.ILog log = log4net.LogManager.GetLogger(typeof(Global));

            try
            {
                Stopwatch sw = Context.Items[StopwatchItemKey] as Stopwatch;
                if (sw != null && sw.IsRunning)
                {
                    sw.Stop();
                }

                if (_diagnosticMode)
                {
                    log.Info(
                        "OTEL REQUEST END => " + Request.HttpMethod + " " + Request.Path +
                        ", HTTP=" + Response.StatusCode +
                        ", DuracionMs=" + (sw == null ? -1 : sw.Elapsed.TotalMilliseconds) +
                        ", TraceId=" + Convert.ToString(Context.Items[TraceIdItemKey]) +
                        ", SpanId=" + Convert.ToString(Context.Items[SpanIdItemKey]) +
                        ", CorrelationId=" + Convert.ToString(Context.Items[CorrelationIdItemKey]));
                }
            }
            catch (Exception ex)
            {
                log.Error("OTEL EndRequest ERROR => " + ex);
            }
            finally
            {
                log4net.LogicalThreadContext.Properties.Remove("trace_id");
                log4net.LogicalThreadContext.Properties.Remove("span_id");
                log4net.LogicalThreadContext.Properties.Remove("correlation_id");
            }
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            log4net.ILog log = log4net.LogManager.GetLogger(typeof(Global));

            try
            {
                Exception ex = Server.GetLastError();
                if (ex == null)
                {
                    return;
                }

                Activity activity = Activity.Current;
                if (activity != null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.SetTag("error", true);
                    activity.SetTag("exception.type", ex.GetType().FullName);
                    activity.SetTag("exception.message", ex.Message);
                }

                if (!(ex is System.Web.UI.ViewStateException))
                {
                    log.Error("ERROR OCURRIDO POR => " + ex);
                }

                if (ex is HttpRequestValidationException)
                {
                    // Se conserva el comportamiento existente de la aplicacion.
                    Response.Redirect("Error.aspx", true);
                    return;
                }
            }
            catch (Exception ex)
            {
                log.Error("LOG => " + ex);
            }
        }

        protected void Session_Start(object sender, EventArgs e)
        {
        }

        protected void Application_AuthenticateRequest(object sender, EventArgs e)
        {
        }

        protected void Session_End(object sender, EventArgs e)
        {
        }

        protected void Application_End(object sender, EventArgs e)
        {
            log4net.ILog log = log4net.LogManager.GetLogger(typeof(Global));

            try
            {
                if (_tracerProvider != null)
                {
                    // IIS concede poco tiempo al reciclar: vaciar antes de liberar.
                    bool flushed = _tracerProvider.ForceFlush(10000);
                    log.Info("OTEL Application_End ForceFlush => Resultado=" + flushed);

                    _tracerProvider.Dispose();
                    _tracerProvider = null;
                }
            }
            catch (Exception ex)
            {
                log.Error("OTEL Application_End ERROR => " + ex);
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static string AppSetting(string key)
        {
            string value = ConfigurationManager.AppSettings[key];
            return value == null ? null : value.Trim();
        }

        /// <summary>
        /// Acepta la URL base de la organizacion o la URL completa de trazas y
        /// devuelve siempre {base}/v1/traces.
        /// </summary>
        private static Uri BuildTracesEndpoint(string rawEndpoint)
        {
            string value = rawEndpoint.Trim();

            if (value.EndsWith("/", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1);
            }

            if (!value.EndsWith("/" + TracesPath, StringComparison.OrdinalIgnoreCase))
            {
                value = value + "/" + TracesPath;
            }

            Uri endpoint;
            if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint))
            {
                throw new ConfigurationErrorsException(
                    "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT no contiene una URL absoluta valida: " + rawEndpoint);
            }

            if (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
            {
                throw new ConfigurationErrorsException(
                    "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT debe ser http o https: " + rawEndpoint);
            }

            return endpoint;
        }

        /// <summary>
        /// Default always_on a proposito. parentbased_always_on respeta el
        /// traceparent entrante y descarta todo si un proxy o RUM manda "-00".
        /// </summary>
        private static Sampler BuildSampler(string samplerName)
        {
            string normalized = string.IsNullOrEmpty(samplerName)
                ? "always_on"
                : samplerName.Trim().ToLowerInvariant();

            switch (normalized)
            {
                case "always_on":
                    return new AlwaysOnSampler();

                case "always_off":
                    return new AlwaysOffSampler();

                case "parentbased_always_on":
                    return new ParentBasedSampler(new AlwaysOnSampler());

                case "parentbased_always_off":
                    return new ParentBasedSampler(new AlwaysOffSampler());

                default:
                    throw new ConfigurationErrorsException(
                        "OTEL_TRACES_SAMPLER no soportado: " + samplerName +
                        ". Use always_on, parentbased_always_on, always_off o parentbased_always_off.");
            }
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "si", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "sí", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deja visible el prefijo y la longitud, nunca la credencial.</summary>
        private static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(vacio)";
            }

            int space = value.IndexOf(' ');
            string scheme = space > 0 ? value.Substring(0, space) : "(sin esquema)";

            return scheme + " ***(" + value.Length + " chars)";
        }

        private static IEnumerable<KeyValuePair<string, object>> ParseResourceAttributes(string rawValue)
        {
            List<KeyValuePair<string, object>> attributes =
                new List<KeyValuePair<string, object>>();

            if (string.IsNullOrEmpty(rawValue))
            {
                return attributes;
            }

            string[] items = rawValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < items.Length; i++)
            {
                int separatorIndex = items[i].IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = items[i].Substring(0, separatorIndex).Trim();
                string value = items[i].Substring(separatorIndex + 1).Trim();

                if (key.Length > 0)
                {
                    attributes.Add(new KeyValuePair<string, object>(key, value));
                }
            }

            return attributes;
        }

        /// <summary>
        /// Formato OTLP: clave=valor separados por coma. Solo se parte en el PRIMER
        /// '=' para que el padding Base64 del valor Basic sobreviva intacto.
        /// </summary>
        private static Dictionary<string, string> ParseHeaders(string rawValue)
        {
            Dictionary<string, string> headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(rawValue))
            {
                return headers;
            }

            string[] items = rawValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < items.Length; i++)
            {
                int separatorIndex = items[i].IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = items[i].Substring(0, separatorIndex).Trim();
                string value = items[i].Substring(separatorIndex + 1).Trim();

                if (key.Length == 0)
                {
                    continue;
                }

                // Solo se desescapa si hay algo que desescapar. Base64 no lleva '%',
                // y UnescapeDataString puede lanzar sobre secuencias mal formadas.
                if (value.IndexOf('%') >= 0)
                {
                    try
                    {
                        value = Uri.UnescapeDataString(value);
                    }
                    catch (UriFormatException)
                    {
                        // Se conserva el valor literal.
                    }
                }

                headers[key] = value;
            }

            return headers;
        }

        /// <summary>
        /// Reporta si OpenObserve acepto o rechazo cada exportacion OTLP. Sin esto,
        /// un 401 o un fallo de TLS se ven exactamente igual que "no hay trafico".
        /// Los errores siempre se registran; los 2xx solo en modo diagnostico.
        /// </summary>
        private sealed class OtlpDiagnosticsHandler : DelegatingHandler
        {
            private readonly bool _verbose;

            public OtlpDiagnosticsHandler(bool verbose)
                : base(new HttpClientHandler())
            {
                _verbose = verbose;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                // Se resuelve por llamada: el logger no se captura en el constructor
                // porque este handler vive tanto como el TracerProvider.
                log4net.ILog log = log4net.LogManager.GetLogger(typeof(Global));

                try
                {
                    HttpResponseMessage response =
                        await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        string body = string.Empty;
                        try
                        {
                            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            body = "(sin cuerpo)";
                        }

                        if (body != null && body.Length > 500)
                        {
                            body = body.Substring(0, 500) + "...";
                        }

                        log.Error(
                            "OTLP EXPORT FAILED => HTTP=" + (int)response.StatusCode +
                            " " + response.ReasonPhrase +
                            ", Endpoint=" + request.RequestUri +
                            ", Body=" + body +
                            " | 401/403=credencial, 404=endpoint u org id, 5xx=lado servidor.");
                    }
                    else if (_verbose)
                    {
                        log.Info(
                            "OTLP EXPORT OK => HTTP=" + (int)response.StatusCode +
                            ", Endpoint=" + request.RequestUri);
                    }

                    return response;
                }
                catch (Exception ex)
                {
                    log.Error(
                        "OTLP EXPORT EXCEPTION => " + ex.GetType().FullName + ": " + ex.Message +
                        " | Suele ser TLS (falta TLS 1.2), DNS o un proxy corporativo.", ex);
                    throw;
                }
            }
        }
    }
}
