using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Collector.Api.Data;
using Collector.Api.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class ClientIpAndRateLimitTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";

    /// <summary>Simulates the TCP peer the API sees (nginx → Next → API all run on loopback).</summary>
    private sealed class PeerAddressFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = peer;
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    private WebApplicationFactory<Program> From(string peer, Action<RateLimitSettings>? limits = null) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IStartupFilter>(new PeerAddressFilter(IPAddress.Parse(peer)));
            if (limits is not null) s.PostConfigure(limits);
        }));

    private static HttpRequestMessage Login(string username, string password, string? forwardedFor) =>
        new(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username, password }),
            Headers = { { "X-Forwarded-For", forwardedFor ?? "" } },
        };

    private static async Task<string?> LastLoginIpAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        return await db.ActivityLogs.Where(l => l.Action == "login")
            .OrderByDescending(l => l.Id).Select(l => l.IpAddress).FirstAsync();
    }

    [Fact]
    public async Task ForwardedFor_FromTheLoopbackProxy_IsTheClientIp()
    {
        await factory.CreateAccountAsync("ip-proxied", Password);
        using var app = From("127.0.0.1");

        (await app.CreateClient().SendAsync(Login("ip-proxied", Password, "203.0.113.7")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LastLoginIpAsync(app)).Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task ForwardedFor_FromAnUntrustedPeer_IsIgnored()
    {
        await factory.CreateAccountAsync("ip-spoofed", Password);
        using var app = From("198.51.100.9");

        await app.CreateClient().SendAsync(Login("ip-spoofed", Password, "203.0.113.7"));

        (await LastLoginIpAsync(app)).Should().Be("198.51.100.9");
    }

    [Fact]
    public async Task Login_IsRateLimitedPerClientIp()
    {
        using var app = From("127.0.0.1", s => s.Login.PermitLimit = 3);
        var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.SendAsync(Login("nobody", "wrong", "203.0.113.10")))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await client.SendAsync(Login("nobody", "wrong", "203.0.113.10")))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await client.SendAsync(Login("nobody", "wrong", "203.0.113.11")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "another client keeps its own budget");
    }

    [Fact]
    public async Task AnonymousRequests_AreLimitedPerIp_ButHealthChecksNeverAre()
    {
        using var app = From("127.0.0.1", s => s.AnonymousPermitLimit = 2);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");

        (await client.GetAsync("/api/auth/jwks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/jwks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/jwks")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AuthenticatedUsers_HaveTheirOwnBudget()
    {
        await factory.CreateAccountAsync("ip-user-budget", Password);
        var login = await factory.LoginAsync("ip-user-budget", Password);
        using var app = From("127.0.0.1", s => s.AnonymousPermitLimit = 1);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.30");
        await client.GetAsync("/api/auth/jwks");
        (await client.GetAsync("/api/auth/jwks")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
