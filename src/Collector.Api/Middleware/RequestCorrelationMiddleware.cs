using Serilog.Context;

namespace Collector.Api.Middleware;

/// <summary>
/// Attaches a correlation id (either from the incoming X-Correlation-Id header or
/// the ASP.NET TraceIdentifier) to every response and pushes it onto the Serilog
/// LogContext so structured logs can be joined to a specific request.
/// </summary>
public class RequestCorrelationMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public RequestCorrelationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
            && !string.IsNullOrWhiteSpace(incoming)
                ? incoming.ToString()
                : context.TraceIdentifier;

        context.TraceIdentifier = correlationId;

        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
            {
                context.Response.Headers[HeaderName] = correlationId;
            }
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
