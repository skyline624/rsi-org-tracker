using System.Text.Json;
using Collector.Api.Errors;
using Collector.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Api.Tests;

public class ExceptionHandlingMiddlewareTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Collector.Api";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<(int Status, JsonElement Body)> RunAsync(Exception ex, string environment = "Production")
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw ex, NullLogger<ExceptionHandlingMiddleware>.Instance, new FakeEnvironment(environment));

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body.RootElement);
    }

    [Theory]
    [InlineData(typeof(NotFoundException), 404)]
    [InlineData(typeof(ConflictException), 409)]
    [InlineData(typeof(ValidationException), 400)]
    [InlineData(typeof(AuthenticationFailedException), 401)]
    [InlineData(typeof(ForbiddenException), 403)]
    public async Task DomainExceptions_MapToTheirStatus_WithTheirSafeMessage(Type type, int expected)
    {
        var ex = (DomainException)Activator.CreateInstance(type, "Organization 'X' not found")!;

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
}
