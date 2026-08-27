import { initTelemetry, identifyUser, options, apiBaseUrl, logContext, openobserveLogs, openobserveRum } from './telemetry.js';
import { callGateway, flows } from './api.js';

// RUM and logs must be initialised before anything else runs, so that errors thrown
// during startup are captured too.
initTelemetry();

identifyUser({
  id: 'op-1042',
  name: 'Claims Operator',
  email: 'operator@dotnet_demo.example',
});

openobserveLogs.logger.info('Console loaded', logContext({ api_base_url: apiBaseUrl }));

// ---- identity panel --------------------------------------------------------
const identity = document.getElementById('identity');
identity.innerHTML = [
  ['service', options.service],
  ['env', options.env],
  ['version', options.version],
  ['application', options.applicationId],
  ['api', apiBaseUrl],
].map(([k, v]) => `<div><dt>${k}</dt><dd>${v}</dd></div>`).join('');

// ---- flow buttons ----------------------------------------------------------
const actions = document.getElementById('actions');
const tbody = document.querySelector('#results tbody');

function addRow({ title, status, durationMs, traceId, ok }) {
  const row = document.createElement('tr');
  row.innerHTML = `
    <td>${new Date().toLocaleTimeString()}</td>
    <td>${title}</td>
    <td class="${ok ? 'ok' : 'err'}">${status}</td>
    <td>${durationMs} ms</td>
    <td class="trace">${traceId ?? 'none'}</td>`;
  tbody.prepend(row);
}

async function runFlow(flow, button) {
  if (button) button.disabled = true;
  const correlationId = `web-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

  try {
    const result = await callGateway(flow.path, { correlationId });
    addRow({ title: flow.title, status: result.status, durationMs: result.durationMs, traceId: result.traceId, ok: result.ok });
  } catch (error) {
    addRow({ title: flow.title, status: 'network error', durationMs: 0, traceId: null, ok: false });
  } finally {
    if (button) button.disabled = false;
  }
}

for (const flow of flows) {
  const button = document.createElement('button');
  button.innerHTML = `${flow.title}<span class="desc">${flow.description}</span>`;
  button.addEventListener('click', () => runFlow(flow, button));
  actions.append(button);
}

// ---- signal buttons --------------------------------------------------------
document.querySelectorAll('[data-signal]').forEach((button) => {
  button.addEventListener('click', async () => {
    const kind = button.dataset.signal;

    if (kind === 'error') {
      // forwardErrorsToLogs is on, so this reaches both RUM and the logs stream.
      throw new Error('Simulated unhandled error from the claims console');
    }

    if (kind === 'log') {
      openobserveLogs.logger.debug('Debug from the console', logContext({ feature: 'signals' }));
      openobserveLogs.logger.info('Info from the console', logContext({ feature: 'signals' }));
      openobserveLogs.logger.warn('Warning from the console', logContext({ feature: 'signals' }));
      openobserveLogs.logger.error('Error from the console', logContext({ feature: 'signals' }));
      openobserveRum.addAction('signals.logs_emitted', { count: 4 });
      return;
    }

    if (kind === 'storm') {
      button.disabled = true;
      for (let i = 0; i < 5; i++) {
        await runFlow(flows[i % flows.length]);
      }
      button.disabled = false;
    }
  });
});
