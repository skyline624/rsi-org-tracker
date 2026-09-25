using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class SmokeTests(ApiFactory factory)
{
    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await factory.CreateClient().GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PlainHttpOnLoopback_IsServedWithoutRedirect()
    {
        // The API only listens on 127.0.0.1 behind the web front: no HTTPS hop, so the
        // front no longer needs NODE_TLS_REJECT_UNAUTHORIZED=0.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Organizations_RequireAuthentication()
    {
        var response = await factory.CreateClient().GetAsync("/api/organizations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        await factory.CreateAccountAsync("smoke-ok", "correct horse battery");

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { username = "smoke-ok", password = "correct horse battery" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body.Should().ContainKeys("accessToken", "refreshToken", "expiresAt");
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsUnauthorized()
    {
        await factory.CreateAccountAsync("smoke-ko", "correct horse battery");

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { username = "smoke-ko", password = "wrong password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
