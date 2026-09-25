using System.Net;
using System.Text.Json;
using Collector.Api.Errors;
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
        catch (DomainException ex)
        {
            // Expected client-side failure (bad credentials, unknown id…): no stack trace.
            _logger.LogInformation(
                "{Status} {Title} for {Method} {Path}: {Message} CorrelationId={CorrelationId}",
                ex.StatusCode, ex.Title, context.Request.Method, context.Request.Path, ex.Message,
                context.TraceIdentifier);
            await WriteProblemAsync(context, ex.StatusCode, ex.Title, ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception for {Method} {Path} CorrelationId={CorrelationId}",
                context.Request.Method, context.Request.Path, context.TraceIdentifier);

            // Only developer mode surfaces the exception message and type — never in production.
            var dev = _env.IsDevelopment();
            await WriteProblemAsync(context, (int)HttpStatusCode.InternalServerError, "Internal Server Error",
                dev ? ex.Message : null, dev ? ex.GetType().FullName : null);
        }
    }

    private async Task WriteProblemAsync(
        HttpContext context, int status, string title, string? detail, string? exceptionType)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{status}",
            Title = title,
            Status = status,
            Instance = context.Request.Path,
            Detail = detail,
        };
        if (exceptionType is not null)
        {
            problem.Extensions["exceptionType"] = exceptionType;
        }
        var correlationId = context.TraceIdentifier;

        problem.Extensions["correlationId"] = correlationId;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, SerializerOptions),
            context.RequestAborted);
    }
}
