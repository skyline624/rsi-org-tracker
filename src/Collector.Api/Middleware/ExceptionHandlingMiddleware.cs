using System.Globalization;
using System.Net;
using System.Text.Json;
using Collector.Api.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Collector.Api.Middleware;

/// <summary>
/// Global exception middleware converting unhandled exceptions into RFC 7807 Problem Details
/// responses. Never leaks the raw exception message or stack trace to clients — those are
/// only emitted via the structured logger. A correlation id (request id) is attached so the
/// client and the log can be joined after the fact. Domain errors can carry a stable
/// code; temporary failures say when to retry; refused request bodies keep their status.
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
            // Expected failure (bad credentials, unknown id, busy tracker…): no stack trace.
            _logger.LogInformation(
                "{Status} {Title} for {Method} {Path}: {Message} CorrelationId={CorrelationId}",
                ex.StatusCode, ex.Title, context.Request.Method, context.Request.Path, ex.Message,
                context.TraceIdentifier);
            var retryAfter = ex is ServiceUnavailableException unavailable ? unavailable.RetryAfterSeconds : (int?)null;
            await WriteProblemAsync(context, ex.StatusCode, ex.Title, ex.Message, null, ex.Code, retryAfter);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        {
            // Kestrel's refusals are client errors. Keep the framework message in the
            // log only, with no stack trace, even in development.
            _logger.LogInformation(
                "{Status} request refused for {Method} {Path}: {Message} CorrelationId={CorrelationId}",
                ex.StatusCode, context.Request.Method, context.Request.Path, ex.Message,
                context.TraceIdentifier);
            var title = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? "Payload Too Large"
                : ReasonPhrases.GetReasonPhrase(ex.StatusCode);
            await WriteProblemAsync(context, ex.StatusCode, title, null, null);
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
        HttpContext context, int status, string title, string? detail, string? exceptionType,
        string? code = null, int? retryAfterSeconds = null)
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
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        if (retryAfterSeconds is { } seconds)
        {
            context.Response.Headers["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture);
        }
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, SerializerOptions),
            context.RequestAborted);
    }
}
