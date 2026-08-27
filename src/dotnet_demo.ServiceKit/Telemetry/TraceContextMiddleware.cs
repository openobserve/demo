using System.Diagnostics;

namespace dotnet_demo.ServiceKit.Telemetry;

/// <summary>
/// Puts the trace context where humans and clients can see it:
///   * pushes trace_id / span_id / correlation_id into the logging scope, so every
///     log line written during the request carries them (console and OTLP alike);
///   * returns them as response headers, so a caller (curl, front-end, load test)
///     can paste the trace id straight into OpenObserve;
///   * echoes an incoming X-Correlation-Id onto the span as app.correlation_id.
/// </summary>
public sealed class TraceContextMiddleware
{
    public const string TraceIdHeader = "X-Trace-Id";
    public const string SpanIdHeader = "X-Span-Id";
    public const string CorrelationIdHeader = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<TraceContextMiddleware> _logger;

    public TraceContextMiddleware(RequestDelegate next, ILogger<TraceContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var activity = Activity.Current;

        var correlationId = context.Request.Headers.TryGetValue(CorrelationIdHeader, out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("n");

        var traceId = activity?.TraceId.ToHexString() ?? string.Empty;
        var spanId = activity?.SpanId.ToHexString() ?? string.Empty;

        activity?.SetTag("app.correlation_id", correlationId);
        activity?.SetTag("app.service", ServiceTelemetry.ServiceName);

        context.Items[CorrelationIdHeader] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[TraceIdHeader] = traceId;
            context.Response.Headers[SpanIdHeader] = spanId;
            context.Response.Headers[CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["trace_id"] = traceId,
            ["span_id"] = spanId,
            ["correlation_id"] = correlationId,
            ["http.route"] = context.Request.Path.Value ?? string.Empty,
            ["http.method"] = context.Request.Method,
        });

        await _next(context);
    }
}

public static class TraceContextMiddlewareExtensions
{
    public static IApplicationBuilder UseTraceContext(this IApplicationBuilder app) =>
        app.UseMiddleware<TraceContextMiddleware>();
}
