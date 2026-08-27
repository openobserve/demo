using System.Diagnostics;
using dotnet_demo.ServiceKit.Telemetry;
using dotnet_demo.ServiceKit.Topology;

namespace dotnet_demo.ServiceKit;

/// <summary>
/// Runs on the entry-point service only. Each iteration starts a fresh root span and
/// drives one of the gateway flows, so OpenObserve continuously receives complete
/// multi-service traces without anyone running curl.
/// </summary>
public sealed class PlatformTrafficDriver : BackgroundService
{
    private readonly ServiceDefinition _definition;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlatformTrafficDriver> _logger;

    public PlatformTrafficDriver(
        ServiceDefinition definition,
        IHttpClientFactory httpClientFactory,
        ILogger<PlatformTrafficDriver> logger)
    {
        _definition = definition;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = Environment.GetEnvironmentVariable("PLATFORM_TRAFFIC") != "off";
        if (!enabled)
        {
            _logger.LogInformation("Platform traffic driver disabled (PLATFORM_TRAFFIC=off)");
            return;
        }

        var intervalMs = int.TryParse(Environment.GetEnvironmentVariable("PLATFORM_TRAFFIC_INTERVAL_MS"), out var v)
            ? v
            : 6_000;

        // Give the rest of the platform time to bind its ports.
        await Task.Delay(12_000, stoppingToken);
        _logger.LogInformation("Platform traffic driver started; one flow every {IntervalMs}ms", intervalMs);

        var routes = _definition.Routes.Select(r => r.Path).ToArray();
        var iteration = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            iteration++;

            // Weighted: claim submissions dominate, matching real traffic shape.
            var path = iteration % 5 == 0
                ? routes[iteration % routes.Length]
                : routes[0];

            using var activity = ServiceTelemetry.Source.StartActivity("synthetic.flow", ActivityKind.Producer);
            activity?.SetTag("app.synthetic", true);
            activity?.SetTag("app.synthetic.iteration", iteration);
            activity?.SetTag("app.synthetic.flow", path);

            try
            {
                var client = _httpClientFactory.CreateClient("downstream");
                using var response = await client.GetAsync(
                    $"http://localhost:{_definition.Port}{path}", stoppingToken);

                var traceId = response.Headers.TryGetValues(TraceContextMiddleware.TraceIdHeader, out var values)
                    ? values.FirstOrDefault()
                    : null;

                _logger.LogInformation(
                    "Synthetic flow {Path} -> {StatusCode} (trace {TraceId})",
                    path, (int)response.StatusCode, traceId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceTelemetry.RecordException(activity, ex);
                _logger.LogError(ex, "Synthetic flow {Path} failed", path);
            }

            try
            {
                await Task.Delay(intervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
