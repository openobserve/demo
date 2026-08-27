# dotnet_demo OpenTelemetry Platform

16 .NET services plus a JavaScript frontend, all sending telemetry to OpenObserve, with
one trace id running from the browser click through to the mainframe adapter.

## Versions

| Service | Port | Target | Runtime |
|---|---|---|---|
| dotnet_demo-api-gateway | 6001 | net6.0 | .NET 6.0.36 |
| dotnet_demo-auth-service | 6002 | net6.0 | .NET 6.0.36 |
| dotnet_demo-member-service | 6003 | net6.0 | .NET 6.0.36 |
| dotnet_demo-provider-service | 6004 | net6.0 | .NET 6.0.36 |
| dotnet_demo-claims-intake | 6005 | net6.0 | .NET 6.0.36 |
| dotnet_demo-claims-validation | 6006 | net6.0 | .NET 6.0.36 |
| dotnet_demo-eligibility-service | 6007 | net6.0 | .NET 6.0.36 |
| dotnet_demo-benefits-service | 6008 | net6.0 | .NET 6.0.36 |
| dotnet_demo-pricing-service | 6009 | net6.0 | .NET 6.0.36 |
| dotnet_demo-adjudication-service | 6010 | net6.0 | .NET 6.0.36 |
| dotnet_demo-payment-service | 6011 | net6.0 | .NET 6.0.36 |
| dotnet_demo-notification-service | 6012 | net6.0 | .NET 6.0.36 |
| dotnet_demo-audit-service | 6013 | net6.0 | .NET 6.0.36 |
| dotnet_demo-document-service | 6014 | net6.0 | .NET 6.0.36 |
| dotnet_demo-reporting-service | 6015 | net6.0 | .NET 6.0.36 |
| dotnet_demo-legacy-mainframe-adapter | 6016 | net472 | .NET Framework 4.7.2, CLR 4.0.30319.42000 |
| dotnet_demo-web (browser console) | 5173 | JavaScript, Vite 5 | Browser, `@openobserve/browser-rum` and `browser-logs` 0.4.1 |

Every service reports its own runtime:

```bash
curl -s http://localhost:6001/ | jq '{service, targetFramework, runtime}'
curl -s http://localhost:6016/ | jq '{service, targetFramework, runtime}'
```

Prerequisites: the .NET 6 runtime at `$HOME/.dotnet6` (override with `DOTNET6`) and Mono
for the .NET Framework build (override with `MONO`).

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- \
  --channel 6.0 --runtime aspnetcore --install-dir $HOME/.dotnet6 --no-path
brew install mono
```

## Running

```bash
./start-platform.sh     # all 16 services
./start-frontend.sh     # browser console on :5173, in a second terminal
./stop-platform.sh
```

Logs go to `logs/<service>.log`. The gateway drives a synthetic claim every 6 seconds. One
`GET /claims/submit` produces a single trace spanning all 16 services, starting in the
browser when the call comes from the console.

## Instrumentation: the 15 net6.0 services

All 15 run from one project, `src/dotnet_demo.Platform`, one process per service, and share
`src/dotnet_demo.ServiceKit`, so they are instrumented identically. OpenTelemetry 1.12.0.

**Traces**

* `AddAspNetCoreInstrumentation` and `AddHttpClientInstrumentation` give server and client
  spans plus W3C `traceparent` propagation between services.
* `WorkStepExecutor` adds hand-written spans for each unit of work: database calls tagged
  with `db.system`, `db.name`, `db.statement`, `db.operation`; cache lookups with
  `cache.key`; queue publishes as producer spans with `messaging.*`; cross-service calls
  with `peer.service`.
* Health and Swagger requests are filtered out. Sampling is parent based at 1.0.

**Metrics**

ASP.NET Core, HttpClient, runtime and process instrumentation, plus six custom instruments
common to every service so one panel can split any of them by `service_name`:

| Instrument | Type | Dimensions |
|---|---|---|
| `dotnet_demo.requests` | counter | `operation`, `outcome` |
| `dotnet_demo.downstream.calls` | counter | `peer.service`, `http.response.status_code` |
| `dotnet_demo.failures` | counter | `kind`, `operation` |
| `dotnet_demo.operation.duration` | histogram, ms | `operation`, `outcome` |
| `dotnet_demo.dependency.duration` | histogram, ms | `dependency.kind`, `peer.service`, `db.operation`, `cache.result` |
| `dotnet_demo.in_flight` | up/down counter | none |

Exemplars are trace based, so a latency bucket links back to a real trace. Export
intervals are jittered per process by 0 to 5 seconds.

**Logs**

`builder.Logging.AddOpenTelemetry(...)` routes every `ILogger` record through OTLP with
`IncludeScopes`, `IncludeFormattedMessage` and `ParseStateValues` on.

**Correlation**

* The OpenTelemetry logging provider stamps each log record with the ambient activity's
  trace and span id.
* `LogEnrichmentProcessor` repeats `trace_id`, `span_id`, `service_name` and
  `deployment.environment` as plain log attributes, and mirrors Warning and above onto the
  span as span events.
* `ResourceContextSpanProcessor` sets `deployment.environment`, `host.name`,
  `service.version` and `service.instance.id` as span attributes. OpenObserve prefixes
  resource attributes with `service_` on the traces path only, so setting them on the span
  keeps field names identical across the logs, metrics and traces streams.
* `TraceContextMiddleware` returns `X-Trace-Id`, `X-Span-Id` and `X-Correlation-Id`, and
  pushes them into the log scope.

## Instrumentation: the net472 service

Same three signals, written by hand because .NET Framework has no ASP.NET Core and no
minimal hosting.

* Providers built with `Sdk.CreateTracerProviderBuilder()` and
  `Sdk.CreateMeterProviderBuilder()`, flushed with `ForceFlush` at shutdown.
* Raw `HttpListener`, so the server span is created manually and **W3C context is extracted
  by hand** with `ActivityContext.TryParse` on the incoming `traceparent` header. That step
  is what puts mainframe calls inside the caller's trace.
* `ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12`, since .NET
  Framework negotiates older protocols by default and the OTLP endpoint rejects them.
* `System.Diagnostics.DiagnosticSource` comes from NuGet rather than in-box.
* `Microsoft.Extensions.Logging` with the OpenTelemetry provider attached, so its logs
  carry trace ids like the rest.
* `LegacyResourceContextProcessor` and `LegacyLogEnrichmentProcessor` mirror the modern
  services' span and log enrichment.
* Spans tagged with `mainframe.transaction`, `mainframe.copybook`, `mainframe.region`, and
  DB2 work with `db.system=db2`.

The project multi-targets `net472;net6.0`. The launcher runs the `net472` build under Mono,
which is how .NET Framework assemblies execute on macOS and Linux; on Windows the same
assembly runs on Microsoft's CLR. The `net6.0` output is only a fallback.

## Instrumentation: the frontend

`frontend/` is a Vite app using `@openobserve/browser-rum` and `@openobserve/browser-logs`.
Initialised in `src/telemetry.js` before anything else runs, so startup errors are captured.

**Identity.** The same three attributes the backend reports, which is what makes the
browser session and the backend trace look like one system:

| Frontend option | OpenObserve field | Backend equivalent |
|---|---|---|
| `service` | `service_name` | `AddService(serviceName)` |
| `env` | `deployment_environment` | `OpenObserve:DeploymentEnvironment` |
| `version` | `service_version` | `OpenObserve:ServiceVersion` |

All overridable at build time with `VITE_SERVICE_NAME`, `VITE_DEPLOYMENT_ENV`,
`VITE_SERVICE_VERSION`, `VITE_API_BASE_URL` and the `VITE_O2_*` connection settings.

**RUM.** `trackResources`, `trackLongTasks` and `trackUserInteractions` are on, session
sample rate 100 percent, session replay 50 percent. `openobserveRum.setUser` identifies the
operator, and each API call adds a custom `api.call` action carrying the backend trace id.

**Logs.** `forwardErrorsToLogs` is on, so uncaught errors reach the logs stream as well as
RUM. Every log record from `src/api.js` carries `service`, `env`, `version`, the URL,
status, duration, `trace_id` and `correlation_id`.

**Trace propagation.** `allowedTracingUrls` matches the API gateway with
`propagatorTypes: ['openobserve', 'tracecontext']`. The `tracecontext` propagator emits the
W3C `traceparent` header, which is exactly what the backend already parses: ASP.NET Core
instrumentation on the 15 net6.0 services, and `ActivityContext.TryParse` in the net472
service. The trace therefore starts in the browser, not at the gateway.

Verified end to end: a browser generated trace id arriving on `/claims/submit` was found in
all 16 backend services, the mainframe adapter included.

**CORS.** The gateway allows the console origin and, critically, the tracing headers. A
missing entry here means the preflight fails and no request arrives at all.

* Allowed request headers: `traceparent`, `tracestate`, `b3`, `x-openobserve-trace-id`,
  `x-openobserve-parent-id`, `x-openobserve-origin`, `x-openobserve-sampling-priority`,
  `X-Correlation-Id`, `Accept`, `Content-Type`.
* Exposed response headers: `X-Trace-Id`, `X-Span-Id`, `X-Correlation-Id`, so browser
  JavaScript can read the backend trace id and log it.
* Origins default to `http://localhost:5173`, override with `Cors__AllowedOrigins`.

## Configuration

`src/dotnet_demo.Platform/appsettings.json`, section `OpenObserve`. Override any key with
`OpenObserve__<Key>`. The net472 service reads the same environment variables and falls
back to compiled-in defaults.

| Key | Default |
|---|---|
| `Endpoint` | `https://api.openobserve.ai/api/3HVdyHWBa7TLxcQ2fLmu1dsjuPx` |
| `Authorization` | the Basic credential |
| `StreamName` | `default` |
| `DeploymentEnvironment` | `local` |
| `MetricExportIntervalMs` | `15000` |
| `BatchScheduledDelayMs` | `5000` |
| `TraceSampleRatio` | `1.0` |
| `VerboseExportLogging` | `false`, failures always log |

Signal paths `/v1/traces`, `/v1/metrics` and `/v1/logs` are appended to `Endpoint`. Auth
headers go through a custom `HttpClientFactory` because the base64 credential contains `=`
padding that the OTLP header parser would mangle.

To make OpenObserve correlate logs with traces, `service_name` has to be in each stream's
`distinct_value_fields`. Run `configure-openobserve.sh` with a user or admin credential;
the ingest token cannot call that API.
