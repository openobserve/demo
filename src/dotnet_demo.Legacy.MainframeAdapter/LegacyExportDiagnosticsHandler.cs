using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace dotnet_demo.Legacy.MainframeAdapter
{
    /// <summary>
    /// Surfaces OTLP ingestion problems from the legacy process. Worth having here in
    /// particular: .NET Framework/Mono use a different TLS stack from modern .NET, so a
    /// handshake failure against the collector shows up as a silent telemetry gap unless
    /// something reports it.
    ///
    /// Writes to the console rather than through ILogger, because logging from inside the
    /// log-export path would feed the log exporter its own output.
    /// </summary>
    internal sealed class LegacyExportDiagnosticsHandler : DelegatingHandler
    {
        private readonly string _signal;
        private readonly bool _verbose;

        public LegacyExportDiagnosticsHandler(string signal, bool verbose)
            : base(new HttpClientHandler())
        {
            _signal = signal;
            _verbose = verbose;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 300)
                    {
                        body = body.Substring(0, 300);
                    }

                    Console.Error.WriteLine("[otlp:" + _signal + "] export FAILED " + (int)response.StatusCode
                        + " " + response.ReasonPhrase + " -> " + request.RequestUri + " :: " + body);
                }
                else if (_verbose)
                {
                    Console.Out.WriteLine("[otlp:" + _signal + "] export ok " + (int)response.StatusCode
                        + " -> " + request.RequestUri);
                }

                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[otlp:" + _signal + "] export threw " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }
    }
}
