import { apiBaseUrl, openobserveRum, openobserveLogs, logContext } from './telemetry.js';

/**
 * Calls the API gateway.
 *
 * The RUM SDK intercepts fetch to allowedTracingUrls and injects the traceparent header,
 * so nothing has to be done here to propagate context. What this function adds is the
 * business correlation id and the reading back of X-Trace-Id, which the gateway returns
 * so the browser can name the exact backend trace in its own logs.
 */
export async function callGateway(path, { correlationId } = {}) {
  const url = `${apiBaseUrl}${path}`;
  const startedAt = performance.now();

  const headers = { Accept: 'application/json' };
  if (correlationId) {
    headers['X-Correlation-Id'] = correlationId;
  }

  openobserveLogs.logger.info(`Calling ${path}`, logContext({ url, correlationId }));

  try {
    const response = await fetch(url, { headers });
    const durationMs = Math.round(performance.now() - startedAt);

    // Readable only because the gateway lists these in Access-Control-Expose-Headers.
    const traceId = response.headers.get('X-Trace-Id');
    const spanId = response.headers.get('X-Span-Id');
    const returnedCorrelationId = response.headers.get('X-Correlation-Id');

    const body = await response.json().catch(() => ({}));

    const context = logContext({
      url,
      status: response.status,
      duration_ms: durationMs,
      trace_id: traceId,
      span_id: spanId,
      correlation_id: returnedCorrelationId,
    });

    if (!response.ok) {
      openobserveLogs.logger.error(`${path} failed with ${response.status}`, context);
      openobserveRum.addError(new Error(`${path} returned ${response.status}`), context);
    } else {
      openobserveLogs.logger.info(`${path} completed in ${durationMs}ms`, context);
    }

    // A custom RUM action carries the backend trace id into the session timeline.
    openobserveRum.addAction('api.call', {
      path,
      status: response.status,
      duration_ms: durationMs,
      trace_id: traceId,
      correlation_id: returnedCorrelationId,
    });

    return { ok: response.ok, status: response.status, traceId, spanId, correlationId: returnedCorrelationId, durationMs, body };
  } catch (error) {
    const durationMs = Math.round(performance.now() - startedAt);
    const context = logContext({ url, duration_ms: durationMs, error: String(error) });

    openobserveLogs.logger.error(`${path} threw: ${error.message}`, context);
    openobserveRum.addError(error, context);
    throw error;
  }
}

export const flows = [
  {
    id: 'submit',
    path: '/claims/submit',
    title: 'Submit claim',
    description: 'Full pipeline across all 16 services, including the .NET Framework mainframe adapter.',
  },
  {
    id: 'lookup',
    path: '/members/lookup',
    title: 'Member lookup',
    description: 'Gateway to auth to member service to the legacy adapter.',
  },
  {
    id: 'report',
    path: '/reports/daily',
    title: 'Daily report',
    description: 'Gateway to auth to reporting service to audit service.',
  },
];
