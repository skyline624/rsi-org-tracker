using Collector.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Collector.Api.Tests;

public sealed class RequestCorrelationMiddlewareTests
{
    [Theory]
    [InlineData("client-id_42:request.1", true)]
    [InlineData("", false)]
    [InlineData("has a space", false)]
    [InlineData("quoted\"value", false)]
    [InlineData("line\r\nbreak", false)]
    [InlineData("é", false)]
    public async Task OnlyBoundedAsciiIdentifiers_AreCopiedToLogs(string incoming, bool valid)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "server-id" };
        context.Request.Headers["X-Correlation-Id"] = incoming;
        await new RequestCorrelationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);
        context.TraceIdentifier.Should().Be(valid ? incoming : "server-id");
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task IdentifierLength_IsBounded(int size, bool valid)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "server-id" };
        context.Request.Headers["X-Correlation-Id"] = new string('A', size);
        await new RequestCorrelationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);
        context.TraceIdentifier.Should().Be(valid ? new string('A', size) : "server-id");
    }

    [Fact]
    public async Task MultipleValues_AreIgnored()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "server-id" };
        context.Request.Headers["X-Correlation-Id"] = new StringValues(["a", "b"]);
        await new RequestCorrelationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);
        context.TraceIdentifier.Should().Be("server-id");
    }
}
