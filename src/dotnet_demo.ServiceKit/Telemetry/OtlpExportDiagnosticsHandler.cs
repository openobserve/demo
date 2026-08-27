namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Wraps the exporter's HTTP calls so ingestion problems are visible immediately
/// (401s, wrong stream, endpoint typos) instead of silently dropping telemetry.
///
/// Writes straight to the console rather than through ILogger on purpose: logging from
/// inside the log-export path would feed the log exporter its own output.
/// </summary>
public sealed class OtlpExportDiagnosticsHandler : DelegatingHandler
{
    private readonly string _signal;
    private readonly bool _verbose;

    public OtlpExportDiagnosticsHandler(string signal, bool verbose)
        : base(new HttpClientHandler())
    {
        _signal = signal;
        _verbose = verbose;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine(
                    $"[otlp:{_signal}] export FAILED {(int)response.StatusCode} {response.ReasonPhrase} -> {request.RequestUri} :: {Truncate(body)}");
            }
            else if (_verbose)
            {
                Console.Out.WriteLine($"[otlp:{_signal}] export ok {(int)response.StatusCode} -> {request.RequestUri}");
            }

            return response;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[otlp:{_signal}] export threw {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}
