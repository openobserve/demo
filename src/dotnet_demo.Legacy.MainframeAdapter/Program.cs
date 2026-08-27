using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace dotnet_demo.Legacy.MainframeAdapter
{
    /// <summary>
    /// A .NET Framework 4.7.2 service fronting a (simulated) CICS/DB2 mainframe.
    ///
    /// This is the legacy tier of the platform: no ASP.NET Core, no dependency injection,
    /// no minimal hosting — an HttpListener loop with hand-written OpenTelemetry
    /// instrumentation, including manual W3C traceparent extraction so its spans join the
    /// traces started by the modern services upstream.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var options = new LegacyOptions
            {
                Endpoint = Env("OpenObserve__Endpoint",
                    "https://api.openobserve.ai/api/3HVdyHWBa7TLxcQ2fLmu1dsjuPx"),
                Authorization = Env("OpenObserve__Authorization",
                    "Basic dXNlckBleGFtcGxlLmNvbTpSRVBMQUNFX1dJVEhfWU9VUl9BUElfS0VZ"),
                StreamName = Env("OpenObserve__StreamName", "default"),
                DeploymentEnvironment = Env("OpenObserve__DeploymentEnvironment", "local"),
                Port = int.Parse(Env("LEGACY_PORT", "6016")),
                VerboseExportLogging = Env("OpenObserve__VerboseExportLogging", "false") == "true",
            };

            LegacyTelemetry.Initialize(options);

            var server = new LegacyHttpServer(options);
            var stop = new ManualResetEventSlim(false);

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };

            AppDomain.CurrentDomain.ProcessExit += (s, e) => stop.Set();

            try
            {
                server.Start();
                LegacyTelemetry.Logger.LogInformation(
                    "{Service} listening on http://localhost:{Port} (runtime {Runtime})",
                    LegacyTelemetry.ServiceName, options.Port, RuntimeLabel());
                stop.Wait();
            }
            catch (Exception ex)
            {
                LegacyTelemetry.Logger.LogCritical(ex, "Legacy adapter failed to start");
                return 1;
            }
            finally
            {
                server.Stop();
                LegacyTelemetry.Logger.LogInformation("Legacy adapter shutting down; flushing telemetry");
                LegacyTelemetry.Shutdown();
            }

            return 0;
        }

        private static string RuntimeLabel()
        {
#if NET472
            return ".NET Framework 4.7.2";
#else
            return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#endif
        }

        private static string Env(string key, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }

    internal sealed class LegacyHttpServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly LegacyOptions _options;
        private readonly Random _random = new Random();
        private bool _running;

        public LegacyHttpServer(LegacyOptions options)
        {
            _options = options;
            _listener.Prefixes.Add("http://localhost:" + options.Port + "/");
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _listener.BeginGetContext(OnContext, null);
        }

        public void Stop()
        {
            _running = false;
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }

        private void OnContext(IAsyncResult result)
        {
            if (!_running)
            {
                return;
            }

            HttpListenerContext context;
            try
            {
                context = _listener.EndGetContext(result);
            }
            catch (Exception)
            {
                return;
            }

            _listener.BeginGetContext(OnContext, null);

            try
            {
                Handle(context);
            }
            catch (Exception ex)
            {
                LegacyTelemetry.Logger.LogError(ex, "Unhandled error serving {Path}", context.Request.Url.AbsolutePath);
                WriteJson(context, 500, "{\"error\":\"internal\"}");
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = context.Request.Url.AbsolutePath.TrimEnd('/');

            if (path == "/health")
            {
                WriteJson(context, 200, "{\"status\":\"healthy\",\"service\":\"" + LegacyTelemetry.ServiceName + "\"}");
                return;
            }

            // --- manual W3C context extraction -------------------------------
            // ASP.NET Core instrumentation does this automatically; on .NET Framework
            // with a raw HttpListener it has to be written out. Without it, every
            // mainframe call would start a brand-new trace and the correlation to the
            // calling service would be lost.
            var parentContext = default(ActivityContext);
            var traceparent = context.Request.Headers["traceparent"];
            if (!string.IsNullOrEmpty(traceparent))
            {
                var tracestate = context.Request.Headers["tracestate"];
                ActivityContext.TryParse(traceparent, tracestate, out parentContext);
            }

            var operation = OperationFor(path);
            using (var activity = LegacyTelemetry.Source.StartActivity(
                       operation, ActivityKind.Server, parentContext))
            {
                var sw = Stopwatch.StartNew();

                if (activity != null)
                {
                    activity.SetTag("http.request.method", context.Request.HttpMethod);
                    activity.SetTag("url.path", path);
                    activity.SetTag("server.port", _options.Port);
                    activity.SetTag("client.address", context.Request.RemoteEndPoint == null
                        ? null : context.Request.RemoteEndPoint.Address.ToString());
                    activity.SetTag("app.caller.service", context.Request.Headers["X-Calling-Service"]);
                    activity.SetTag("app.correlation_id", context.Request.Headers["X-Correlation-Id"]);
                }

                // A message-format scope rather than a raw KeyValuePair[]: the console
                // formatter renders the values (so `grep <trace id> logs/*.log` finds this
                // service too) and the OTLP exporter still gets them as structured
                // attributes because ParseStateValues/IncludeScopes are on.
                using (LegacyTelemetry.Logger.BeginScope(
                           "trace_id:{TraceId} span_id:{SpanId}",
                           activity == null ? "" : activity.TraceId.ToHexString(),
                           activity == null ? "" : activity.SpanId.ToHexString()))
                {
                    try
                    {
                        var body = Dispatch(path, activity);

                        sw.Stop();
                        LegacyTelemetry.Transactions.Add(1,
                            new System.Collections.Generic.KeyValuePair<string, object>("operation", operation),
                            new System.Collections.Generic.KeyValuePair<string, object>("outcome", "success"));
                        LegacyTelemetry.OperationDuration.Record(sw.Elapsed.TotalMilliseconds,
                            new System.Collections.Generic.KeyValuePair<string, object>("operation", operation),
                            new System.Collections.Generic.KeyValuePair<string, object>("outcome", "success"));

                        if (activity != null)
                        {
                            activity.SetTag("http.response.status_code", 200);
                            activity.SetStatus(ActivityStatusCode.Ok);
                        }

                        LegacyTelemetry.Logger.LogInformation(
                            "{Operation} completed in {ElapsedMs:F1}ms for caller {Caller}",
                            operation, sw.Elapsed.TotalMilliseconds,
                            context.Request.Headers["X-Calling-Service"] ?? "unknown");

                        WriteJson(context, 200, body);
                    }
                    catch (MainframeException ex)
                    {
                        sw.Stop();
                        LegacyTelemetry.Failures.Add(1,
                            new System.Collections.Generic.KeyValuePair<string, object>("kind", "mainframe"),
                            new System.Collections.Generic.KeyValuePair<string, object>("abend.code", ex.AbendCode));
                        LegacyTelemetry.OperationDuration.Record(sw.Elapsed.TotalMilliseconds,
                            new System.Collections.Generic.KeyValuePair<string, object>("operation", operation),
                            new System.Collections.Generic.KeyValuePair<string, object>("outcome", "failure"));

                        if (activity != null)
                        {
                            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                            activity.SetTag("mainframe.abend_code", ex.AbendCode);
                            activity.SetTag("http.response.status_code", 502);
                            activity.SetTag("error", true);
                        }

                        LegacyTelemetry.Logger.LogError(ex,
                            "{Operation} failed with mainframe abend {AbendCode}", operation, ex.AbendCode);

                        WriteJson(context, 502, "{\"error\":\"" + ex.AbendCode + "\",\"detail\":\"" + ex.Message + "\"}");
                    }
                }
            }
        }

        private string Dispatch(string path, Activity activity)
        {
            switch (path)
            {
                case "/mainframe/member":
                    return MemberMaster(activity);
                case "/mainframe/eligibility":
                    return EligibilityInquiry(activity);
                default:
                    return "{\"service\":\"" + LegacyTelemetry.ServiceName
                        + "\",\"runtime\":\"" + RuntimeLabelPublic()
                        + "\",\"targetFramework\":\"net472\""
                        + ",\"routes\":[\"/mainframe/member\",\"/mainframe/eligibility\",\"/health\"]}";
            }
        }

        /// <summary>Simulated CICS transaction against the member master file.</summary>
        private string MemberMaster(Activity parent)
        {
            using (var cics = LegacyTelemetry.Source.StartActivity("CICS MBRQ01", ActivityKind.Client))
            {
                TagMainframe(cics, "MBRQ01", "MBRMAST", "VSAM");
                var sw = Stopwatch.StartNew();
                Thread.Sleep(_random.Next(35, 140));
                sw.Stop();

                LegacyTelemetry.DependencyDuration.Record(sw.Elapsed.TotalMilliseconds,
                    new System.Collections.Generic.KeyValuePair<string, object>("dependency.kind", "mainframe"),
                    new System.Collections.Generic.KeyValuePair<string, object>("mainframe.transaction", "MBRQ01"));

                // Legacy systems fail in legacy ways.
                if (_random.NextDouble() < 0.04)
                {
                    throw new MainframeException("S0C7", "data exception in COBOL module MBRMAST01");
                }

                if (cics != null)
                {
                    cics.SetTag("mainframe.records_returned", 1);
                }

                return "{\"memberId\":\"M" + _random.Next(100000, 999999)
                    + "\",\"source\":\"MBRMAST\",\"transaction\":\"MBRQ01\"}";
            }
        }

        /// <summary>Simulated DB2 eligibility inquiry.</summary>
        private string EligibilityInquiry(Activity parent)
        {
            using (var db2 = LegacyTelemetry.Source.StartActivity("SELECT ELIG_SPAN", ActivityKind.Client))
            {
                if (db2 != null)
                {
                    db2.SetTag("db.system", "db2");
                    db2.SetTag("db.name", "PRODELIG");
                    db2.SetTag("db.operation", "SELECT");
                    db2.SetTag("db.sql.table", "ELIG_SPAN");
                    db2.SetTag("db.statement", "SELECT ELIG_CD, EFF_DT, END_DT FROM PRODELIG.ELIG_SPAN WHERE MBR_ID = ?");
                    db2.SetTag("server.address", "mvs01.dotnet_demo.internal");
                    db2.SetTag("mainframe.region", "CICSPRD1");
                }

                var sw = Stopwatch.StartNew();
                Thread.Sleep(_random.Next(45, 210));
                sw.Stop();

                LegacyTelemetry.DependencyDuration.Record(sw.Elapsed.TotalMilliseconds,
                    new System.Collections.Generic.KeyValuePair<string, object>("dependency.kind", "mainframe"),
                    new System.Collections.Generic.KeyValuePair<string, object>("db.system", "db2"));

                if (_random.NextDouble() < 0.05)
                {
                    throw new MainframeException("SQL0911N", "deadlock or timeout on PRODELIG.ELIG_SPAN, rollback performed");
                }

                return "{\"eligible\":true,\"planCode\":\"MCD-STD\",\"source\":\"DB2/PRODELIG\"}";
            }
        }

        private static void TagMainframe(Activity activity, string transaction, string copybook, string store)
        {
            if (activity == null)
            {
                return;
            }

            activity.SetTag("mainframe.transaction", transaction);
            activity.SetTag("mainframe.copybook", copybook);
            activity.SetTag("mainframe.region", "CICSPRD1");
            activity.SetTag("db.system", store.ToLowerInvariant());
            activity.SetTag("server.address", "mvs01.dotnet_demo.internal");
        }

        internal static string RuntimeLabelPublic()
        {
#if NET472
            return ".NET Framework 4.7.2 (CLR " + Environment.Version + ")";
#else
            return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
#endif
        }

        private static string OperationFor(string path)
        {
            switch (path)
            {
                case "/mainframe/member": return "MemberMasterInquiry";
                case "/mainframe/eligibility": return "EligibilityInquiry";
                default: return "AdapterInfo";
            }
        }

        private static void WriteJson(HttpListenerContext context, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;

            var activity = Activity.Current;
            if (activity != null)
            {
                context.Response.Headers["X-Trace-Id"] = activity.TraceId.ToHexString();
                context.Response.Headers["X-Span-Id"] = activity.SpanId.ToHexString();
            }

            using (var output = context.Response.OutputStream)
            {
                output.Write(bytes, 0, bytes.Length);
            }
        }
    }

    internal sealed class MainframeException : Exception
    {
        public MainframeException(string abendCode, string message) : base(message)
        {
            AbendCode = abendCode;
        }

        public string AbendCode { get; private set; }
    }
}
