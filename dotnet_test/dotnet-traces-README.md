# OpenTelemetry Traces for Classic ASP.NET → OpenObserve

Manual OpenTelemetry instrumentation for **ASP.NET (classic, .NET Framework 4.6.2+)** running
under IIS, exporting traces to OpenObserve Cloud over OTLP/HTTP.

This is the companion to [`dotnet-traces-fixed.cs`](dotnet-traces-fixed.cs) — a drop-in
replacement for `Global.asax.cs`.

It exists because the obvious approach silently produces **no traces at all**, with no error
anywhere, for four independent reasons. Each one is handled below.

---

## Why this isn't just "add the NuGet package"

Classic ASP.NET on .NET Framework hits a specific set of traps. All of them fail *silently* —
the app runs normally, logs look clean, and nothing arrives in OpenObserve.

| # | Trap | Why it's silent | Fix |
|---|------|-----------------|-----|
| 1 | **OTLP defaults to gRPC** | The SDK's default protocol is gRPC. Against an HTTP `/v1/traces` URL it just fails. | `options.Protocol = OtlpExportProtocol.HttpProtobuf` |
| 2 | **TLS 1.2 not negotiated** | .NET Framework may negotiate TLS 1.0 depending on the app's `httpRuntime` target. The handshake against `api.openobserve.ai` fails before any HTTP status exists. | `ServicePointManager.SecurityProtocol \|= SecurityProtocolType.Tls12` |
| 3 | **Base64 `=` padding mangled** | Passing Basic auth via `OTEL_EXPORTER_OTLP_HEADERS` runs it through a `key=value` parser that corrupts the credential's trailing `=`. Result is a 401 you never see. | Attach headers to the `HttpClient` directly, never `options.Headers` |
| 4 | **No export diagnostics** | The OTLP exporter swallows failures by design. A 401, a 404 and "no traffic at all" look identical. | A `DelegatingHandler` that logs status + body |

A fifth one bites during bring-up:

> **`parentbased_always_on` honours the caller's sampling decision.** If any proxy, CDN or
> browser RUM injects a `traceparent` ending in `-00`, every span is dropped while everything
> else looks perfectly healthy. Start with `always_on`, switch to parent-based only once
> traces are confirmed flowing.

### You do **not** need auto-instrumentation

A common piece of bad advice is that the `OTEL_*` keys "do nothing" without the
`OpenTelemetry.AutoInstrumentation` package. That is wrong here.

`AddAspNetInstrumentation()` + the `TelemetryHttpModule` **is** the supported manual path for
classic ASP.NET. Installing auto-instrumentation on top of this file produces **duplicate
spans**. Don't.

### `<appSettings>` are not environment variables

`ConfigurationManager.AppSettings` and `Environment.GetEnvironmentVariable` are different
stores. The SDK never reads your `<appSettings>` section. Only keys this file explicitly reads
by name have any effect — anything else is inert decoration. That's why every key is read and
validated up front, and why an unreadable config throws with a message rather than starting a
half-configured provider.

---

## Setup

### 1. NuGet packages

Keep every `OpenTelemetry.*` package on the **same core version**. The classic-ASP.NET
instrumentation only ships as a `-beta`; use the beta matching your core version. Check
nuget.org for current versions.

```
OpenTelemetry
OpenTelemetry.Exporter.OpenTelemetryProtocol
OpenTelemetry.Instrumentation.AspNet                      (-beta)
OpenTelemetry.Instrumentation.AspNet.TelemetryHttpModule  (-beta)
OpenTelemetry.Instrumentation.Http
System.Diagnostics.DiagnosticSource
```

### 2. Global.asax.cs

Replace it with `dotnet-traces-fixed.cs`.

> **The `namespace` must match the `Inherits=` attribute in `Global.asax`.** If they disagree,
> `Application_Start` never runs and you get zero output with no error. The file ships as
> `namespace SSCNE`; change only that line.

### 3. `Web.config` → `<appSettings>`

```xml
<!-- Contains an ingestion credential. Do not commit to source control. -->
<add key="OTEL_SERVICE_NAME"                  value="SSCNE-WEB" />
<add key="OTEL_RESOURCE_ATTRIBUTES"           value="service.namespace=SSCNE,deployment.environment.name=test,service.version=1.0.0" />
<add key="OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" value="https://api.openobserve.ai/api/YOUR_ORG_ID/v1/traces" />
<add key="OTEL_OPENOBSERVE_AUTHORIZATION"     value="Basic PASTE_INGESTION_TOKEN_HERE" />
<add key="OTEL_STREAM_NAME"                   value="dotnet" />
<add key="OTEL_TRACES_SAMPLER"                value="always_on" />
<add key="OTEL_DIAGNOSTIC_MODE"               value="true" />
<add key="OTEL_STARTUP_TEST"                  value="true" />
```

### 4. `Web.config` → `<system.webServer><modules>`

```xml
<add name="TelemetryHttpModule"
     type="OpenTelemetry.Instrumentation.AspNet.TelemetryHttpModule, OpenTelemetry.Instrumentation.AspNet.TelemetryHttpModule"
     preCondition="integratedMode,managedHandler" />
```

The type name and assembly name being identical looks like a copy-paste error. It is correct.

The application pool must be in **Integrated** mode, not Classic.

### 5. Recycle the app pool

---

## Configuration reference

| Key | Required | Default | Notes |
|-----|----------|---------|-------|
| `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | **yes** | — | Base org URL or full `/v1/traces` URL; both accepted |
| `OTEL_OPENOBSERVE_AUTHORIZATION` | **yes**¹ | — | `Basic <base64>`, copied whole from the UI |
| `OTEL_SERVICE_NAME` | no | `SSCNE-WEB` | Becomes `service.name` |
| `OTEL_RESOURCE_ATTRIBUTES` | no | — | `k=v,k=v`; `service.version` and `deployment.environment[.name]` are extracted |
| `OTEL_STREAM_NAME` | no | `dotnet` | Sent as the `stream-name` header |
| `OTEL_TRACES_SAMPLER` | no | `always_on` | `always_on`, `parentbased_always_on`, `always_off`, `parentbased_always_off` |
| `OTEL_DIAGNOSTIC_MODE` | no | `false` | Synchronous per-span export + verbose logging. **Never leave on in production.** |
| `OTEL_STARTUP_TEST` | no | `false` | Emits one span and force-flushes at startup |
| `OTEL_EXPORTER_OTLP_HEADERS` | no | — | Fallback for the credential; `OTEL_OPENOBSERVE_AUTHORIZATION` wins |

¹ Via either key. Missing both throws a logged `ConfigurationErrorsException`.

### Endpoints

```
Base:     https://api.openobserve.ai/api/{ORG_ID}
  traces  {base}/v1/traces      ← wired by this file
  metrics {base}/v1/metrics
  logs    {base}/v1/logs
```

All OTLP over HTTP/protobuf. Org id and token both come from
**Ingestion → Custom → OpenTelemetry** in the OpenObserve UI, where the Authorization value is
already Base64-encoded.

---

## Verifying it works

Set `OTEL_STARTUP_TEST=true` and `OTEL_DIAGNOSTIC_MODE=true`, recycle the pool, and read the
log4net output **in this order**. The startup test emits a span and force-flushes at boot, so
it validates exporter, credential, TLS and network **without any HTTP traffic and without the
module even loading** — which is what separates "can't reach OpenObserve" from "not producing
spans".

| Log line | Meaning |
|----------|---------|
| `OTEL INICIO OK` | Config loaded. Check the masked `Auth=` and `Stream=` on this line. |
| `ERROR AL INICIAR` | Config invalid — nothing started. Read the exception. |
| `STARTUP TEST FORCEFLUSH => Resultado=True` | Exporter, credential, TLS and network are all good. |
| `OTLP EXPORT FAILED => HTTP=401` / `403` | Credential wrong. |
| `OTLP EXPORT FAILED => HTTP=404` | Endpoint or org id wrong. |
| `OTLP EXPORT EXCEPTION` | TLS 1.2, DNS, or a corporate proxy. |
| `Activity.Current es NULL` | `TelemetryHttpModule` isn't loading. **Only meaningful once the rows above pass.** |

Then hit a page and confirm request spans appear.

**Finally, set `OTEL_DIAGNOSTIC_MODE=false`.** It forces `ExportProcessorType.Simple`, making
every span a blocking HTTPS POST on the request thread. Correct for diagnosis, unacceptable in
production.

---

## What you get

Per request, a `SERVER` span from `TelemetryHttpModule`, plus `CLIENT` spans for any outgoing
`HttpClient`/`HttpWebRequest` call with `traceparent` propagated automatically.

Each server span is tagged with `app.correlation_id`, `app.service` and
`deployment.environment`. Unhandled exceptions set span status to `Error` with
`exception.type` / `exception.message`.

`X-Trace-Id`, `X-Span-Id` and `X-Correlation-Id` are returned as response headers so QA can
quote an ID straight from the browser. The same three land in log4net's
`LogicalThreadContext` (`trace_id`, `span_id`, `correlation_id`), so every log line emitted
during a request carries them.

### Known limitation: traces only

**Logs and metrics are not exported.** The log4net correlation IDs are stamped, but nothing
ships those log lines to OpenObserve — they stay in local files. You get traces in the UI and
logs on disk, with no way to pivot between them.

Closing that gap means adding an OTLP log exporter alongside the tracer provider (the same
resource, the same endpoint with `/v1/logs`). See
`src/dotnet_demo.Legacy.MainframeAdapter/LegacyTelemetry.cs` in this repo for a .NET Framework
example wiring all three signals.

---

## Security

The `Web.config` snippet holds a live ingestion credential.

- Keep it out of source control — `Web.config` transforms, a machine-level config, or IIS
  environment variables.
- The startup log deliberately prints the credential **masked** (`Basic ***(64 chars)`), so
  you can confirm what loaded without writing the secret to a log file.
- If a token is ever committed, rewriting git history is not sufficient. **Rotate it.**
  Anyone who cloned the repo already has it, and hosting providers keep unreachable commits
  addressable by SHA for some time afterward.

---

## Files

| File | Purpose |
|------|---------|
| `dotnet-traces-fixed.cs` | The instrumentation. Drop-in `Global.asax.cs`. |
| `dotnet.txt` | Prior iteration, kept for reference. Superseded. |
| `dotnet-traces-README.md` | This document. |
