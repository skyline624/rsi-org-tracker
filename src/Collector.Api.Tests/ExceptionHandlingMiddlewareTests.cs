using System.Text.Json;
using Collector.Api.Errors;
using Collector.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>Checks the wire shape and logging of expected failures and server errors.</summary>
public class ExceptionHandlingMiddlewareTests
{
    /// <summary>Controls whether server error details are safe to show.</summary>
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Collector.Api";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Records logging severity and whether a stack trace was attached.</summary>
    private sealed class RecordingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }

    private static async Task<(HttpResponse Response, JsonElement Body)> RunForResponseAsync(
        Exception ex, string environment = "Production", ILogger<ExceptionHandlingMiddleware>? logger = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw ex, logger ?? NullLogger<ExceptionHandlingMiddleware>.Instance, new FakeEnvironment(environment));

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response, body.RootElement.Clone());
    }

    private static async Task<(int Status, JsonElement Body)> RunAsync(Exception ex, string environment = "Production")
    {
        var (response, body) = await RunForResponseAsync(ex, environment);
        return (response.StatusCode, body);
    }

    // Reflection does not supply optional constructor arguments, so exercise the same
    // one-argument calls the existing domain code makes.
    private static DomainException CreateDomainException(Type type, string message) => type.Name switch
    {
        nameof(NotFoundException) => new NotFoundException(message),
        nameof(ConflictException) => new ConflictException(message),
        nameof(ValidationException) => new ValidationException(message),
        nameof(AuthenticationFailedException) => new AuthenticationFailedException(message),
        nameof(ForbiddenException) => new ForbiddenException(message),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    [Theory]
    [InlineData(typeof(NotFoundException), 404)]
    [InlineData(typeof(ConflictException), 409)]
    [InlineData(typeof(ValidationException), 400)]
    [InlineData(typeof(AuthenticationFailedException), 401)]
    [InlineData(typeof(ForbiddenException), 403)]
    public async Task DomainExceptions_MapToTheirStatus_WithTheirSafeMessage(Type type, int expected)
    {
        var ex = CreateDomainException(type, "Organization 'X' not found");

        var (status, body) = await RunAsync(ex);

        status.Should().Be(expected);
        body.GetProperty("detail").GetString().Should().Be("Organization 'X' not found");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task FrameworkExceptions_AreServerErrors_NotClientErrors(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type, "A second operation was started on this context")!;

        var (status, _) = await RunAsync(ex);

        status.Should().Be(500);
    }

    [Fact]
    public async Task ServerErrors_InProduction_RevealNeitherMessageNorType()
    {
        var (_, body) = await RunAsync(new InvalidOperationException("SQLite Error 5: database is locked /home/ubuntu/x"));

        body.TryGetProperty("detail", out _).Should().BeFalse();
        body.TryGetProperty("exceptionType", out _).Should().BeFalse();
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConflictCode_IsWrittenAlongsideTheStandardProblemFields()
    {
        var (response, body) = await RunForResponseAsync(
            new ConflictException("A newer sync arrived during your collection", "stale_sync"));

        response.StatusCode.Should().Be(409);
        response.ContentType.Should().Be("application/problem+json");
        body.GetProperty("code").GetString().Should().Be("stale_sync");
        body.GetProperty("detail").GetString().Should().Be("A newer sync arrived during your collection");
        foreach (var name in new[] { "type", "title", "status", "instance", "correlationId" })
        {
            body.TryGetProperty(name, out _).Should().BeTrue($"the problem carries '{name}'");
        }
    }

    [Fact]
    public async Task ValidationCode_IsWrittenAsTheCodeExtension()
    {
        var (status, body) = await RunAsync(new ValidationException("guild.id differs from the route", "invalid_sync"));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("invalid_sync");
    }

    [Fact]
    public async Task WithoutACode_NoCodeOrRetryAfterIsWritten()
    {
        var (response, body) = await RunForResponseAsync(new NotFoundException("Guild not found"));

        body.TryGetProperty("code", out _).Should().BeFalse();
        response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    [Fact]
    public async Task ServiceUnavailable_Is503_WithRetryAfter_AndTheBusyCode()
    {
        var logger = new RecordingLogger();
        var (response, body) = await RunForResponseAsync(new ServiceUnavailableException("The tracker is busy", 30), logger: logger);

        response.StatusCode.Should().Be(503);
        response.Headers["Retry-After"].ToString().Should().Be("30");
        body.GetProperty("title").GetString().Should().Be("Service Unavailable");
        body.GetProperty("detail").GetString().Should().Be("The tracker is busy");
        body.GetProperty("code").GetString().Should().Be("busy");
        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Information, (Exception?)null));
    }

    [Theory]
    [InlineData("guild_busy")]
    [InlineData(null)]
    public async Task ServiceUnavailable_AllowsACustomCode_OrNoCode(string? code)
    {
        var (response, body) = await RunForResponseAsync(new ServiceUnavailableException("Try again later", 5, code));

        response.StatusCode.Should().Be(503);
        response.Headers["Retry-After"].ToString().Should().Be("5");
        if (code is null)
            body.TryGetProperty("code", out _).Should().BeFalse();
        else
            body.GetProperty("code").GetString().Should().Be(code);
    }

    [Theory]
    [InlineData("Production", 413, "Payload Too Large")]
    [InlineData("Development", 413, "Payload Too Large")]
    [InlineData("Development", 400, "Bad Request")]
    public async Task RefusedRequest_KeepsItsStatus_WithoutExposingFrameworkDetails(
        string environment, int status, string title)
    {
        var logger = new RecordingLogger();
        var (response, body) = await RunForResponseAsync(
            new BadHttpRequestException("Request refused with internal framework details", status), environment, logger);

        response.StatusCode.Should().Be(status);
        body.GetProperty("status").GetInt32().Should().Be(status);
        body.GetProperty("title").GetString().Should().Be(title);
        body.TryGetProperty("detail", out _).Should().BeFalse();
        body.TryGetProperty("code", out _).Should().BeFalse();
        body.TryGetProperty("exceptionType", out _).Should().BeFalse();
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Information, (Exception?)null));
    }
}
