import { openobserveRum } from '@openobserve/browser-rum';
import { openobserveLogs } from '@openobserve/browser-logs';

// Everything is overridable at build time with VITE_* environment variables so the same
// bundle can be pointed at a different organization or environment.
const env = import.meta.env;

export const options = {
  clientToken: env.VITE_O2_CLIENT_TOKEN ?? 'rumQrv7NLyHRE1RADeu',
  applicationId: env.VITE_O2_APPLICATION_ID ?? 'dotnet_demo-web',
  site: env.VITE_O2_SITE ?? 'api.openobserve.ai',
  organizationIdentifier: env.VITE_O2_ORG ?? '3HVdyHWBa7TLxcQ2fLmu1dsjuPx',

  // These three are the correlation keys. They must line up with what the .NET services
  // report, otherwise the browser session and the backend trace look like different
  // systems in OpenObserve:
  //   service -> service_name        (backend: OpenObserve:ServiceName / AddService)
  //   env     -> deployment_environment (backend: OpenObserve:DeploymentEnvironment)
  //   version -> service_version     (backend: OpenObserve:ServiceVersion)
  service: env.VITE_SERVICE_NAME ?? 'dotnet_demo-web',
  env: env.VITE_DEPLOYMENT_ENV ?? 'local',
  version: env.VITE_SERVICE_VERSION ?? '1.0.0',

  apiVersion: 'v1',
  insecureHTTP: false,
};

// The API gateway. Requests to this origin get tracing headers injected, which is what
// joins a browser action to the 15-service backend trace.
export const apiBaseUrl = env.VITE_API_BASE_URL ?? 'http://localhost:6001';

export function initTelemetry() {
  openobserveRum.init({
    applicationId: options.applicationId,
    clientToken: options.clientToken,
    site: options.site,
    organizationIdentifier: options.organizationIdentifier,
    service: options.service,
    env: options.env,
    version: options.version,
    apiVersion: options.apiVersion,
    insecureHTTP: options.insecureHTTP,

    trackResources: true,
    trackLongTasks: true,
    trackUserInteractions: true,
    defaultPrivacyLevel: 'allow',

    // End to end correlation. 'tracecontext' emits the W3C traceparent header, which is
    // exactly what the .NET services parse (ASP.NET Core instrumentation on the 15 modern
    // services, ActivityContext.TryParse in the net472 service). 'openobserve' adds the
    // vendor headers so RUM sessions link to the same trace inside OpenObserve.
    allowedTracingUrls: [
      {
        match: apiBaseUrl,
        propagatorTypes: ['openobserve', 'tracecontext'],
      },
    ],

    sessionSampleRate: 100,
    // 100, not 50, on purpose. The SDK sets session.has_replay to true or leaves it
    // undefined (sessionContext.js: `recorderApi.getReplayStats(view.id) ? true : void 0`),
    // and an undefined field is dropped from the payload. At 50 percent, sessions that are
    // not sampled never emit the field, so OpenObserve never creates the
    // session_has_replay column and any query touching it fails with "unknown field".
    // Recording every session guarantees the column exists.
    sessionReplaySampleRate: Number(env.VITE_O2_REPLAY_SAMPLE_RATE ?? 100),
  });

  openobserveLogs.init({
    clientToken: options.clientToken,
    site: options.site,
    organizationIdentifier: options.organizationIdentifier,
    service: options.service,
    env: options.env,
    version: options.version,
    apiVersion: options.apiVersion,
    insecureHTTP: options.insecureHTTP,
    forwardErrorsToLogs: true,
    sessionSampleRate: 100,
  });

  openobserveRum.startSessionReplayRecording();

  return { rum: openobserveRum, logs: openobserveLogs };
}

/**
 * Identifies the operator using the console. Shows up in RUM session search and is
 * attached to every log record from this session.
 */
export function identifyUser(user) {
  openobserveRum.setUser(user);
  openobserveLogs.setUser?.(user);
}

/**
 * Adds the same service/env/version triple to a log record's context, so a log line is
 * self describing even when read outside a session.
 */
export function logContext(extra = {}) {
  return {
    service: options.service,
    env: options.env,
    version: options.version,
    ...extra,
  };
}

export { openobserveRum, openobserveLogs };
