using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Middleware;

/// <summary>
/// Global exception middleware converting unhandled exceptions into RFC 7807 Problem Details
/// responses. Never leaks the raw exception message or stack trace to clients — those are
/// only emitted via the structured logger. A correlation id (request id) is attached so the
/// client and the log can be joined after the fact.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — do not log as an error.
        }
        catch (Exception ex)
        {
            var correlationId = context.TraceIdentifier;
            _logger.LogError(ex,
                "Unhandled exception for {Method} {Path} CorrelationId={CorrelationId}",
                context.Request.Method, context.Request.Path, correlationId);
            await WriteProblemAsync(context, ex, correlationId);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception ex, string correlationId)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var (status, title) = ex switch
        {
            UnauthorizedAccessException => ((int)HttpStatusCode.Unauthorized, "Unauthorized"),
            KeyNotFoundException => ((int)HttpStatusCode.NotFound, "Not Found"),
            ArgumentException => ((int)HttpStatusCode.BadRequest, "Bad Request"),
            InvalidOperationException => ((int)HttpStatusCode.Conflict, "Conflict"),
            _ => ((int)HttpStatusCode.InternalServerError, "Internal Server Error"),
        };

        var problem = new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{status}",
            Title = title,
            Status = status,
            Instance = context.Request.Path,
        };

        // Only developer mode surfaces the exception message and type — never in production.
        if (_env.IsDevelopment())
        {
            problem.Detail = ex.Message;
            problem.Extensions["exceptionType"] = ex.GetType().FullName;
        }

        problem.Extensions["correlationId"] = correlationId;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, SerializerOptions),
            context.RequestAborted);
    }
}
