using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Extensions;
using Collector.Api.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
/// <summary>Checks trusted client addresses and independent global and endpoint budgets.</summary>
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
    public async Task FailedLogins_DoNotUseUpTheRefreshBudget()
    {
        // Behind one address (an office, a family), someone mistyping a password must not
        // stop the others' sessions from being renewed.
        await factory.CreateAccountAsync("ip-refresh-budget", Password);
        var session = await factory.LoginAsync("ip-refresh-budget", Password);
        using var app = From("127.0.0.1", s => s.Login.PermitLimit = 2);
        var client = app.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            await client.SendAsync(Login("nobody", "wrong", "203.0.113.40"));
        }
        (await client.SendAsync(Login("nobody", "wrong", "203.0.113.40")))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = session.RefreshToken }),
            Headers = { { "X-Forwarded-For", "203.0.113.40" } },
        };
        (await client.SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnonymousRequests_AreLimitedPerIp_ButHealthChecksNeverAre()
    {
        using var app = From("127.0.0.1", s => s.AnonymousPermitLimit = 2);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");

        (await client.GetAsync("/api/auth/jwks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/jwks")).StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = await client.GetAsync("/api/auth/jwks");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();
        rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
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

    [Fact]
    public async Task IngestTraffic_CannotSpendTheOwnersSiteAndRevocationBudget()
    {
        using var owner = await factory.SignedInClientAsync("ip-ingest-isolation");
        var created = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = "isolated-ingest", scope = "discord:ingest", expiresAt = DateTime.UtcNow.AddDays(30),
        });
        created.EnsureSuccessStatusCode();
        var key = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var app = From("127.0.0.1", s => s.UserPermitLimit = 2);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", key.GetProperty("rawKey").GetString());
        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync("/api/ingest/discord/guilds/invalid/syncs", new { }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/ingest/discord/guilds/invalid/syncs", new { }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        client.DefaultRequestHeaders.Remove("x-api-key");
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"/api/api-keys/{key.GetProperty("id").GetInt64()}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/refresh")]
    public async Task RejectedAuthenticationRequests_SayWhenToRetry_InSeconds(string path)
    {
        using var app = From("127.0.0.1", s => s.Login.PermitLimit = 1);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.50");
        var payload = path.EndsWith("login", StringComparison.Ordinal)
            ? (object)new { username = "nobody", password = "wrong" }
            : new { refreshToken = "invalid-refresh-token" };
        await client.PostAsJsonAsync(path, payload);

        var rejected = await client.PostAsJsonAsync(path, payload);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();
        rejected.Headers.RetryAfter!.Delta.Should().NotBeNull();
        rejected.Headers.RetryAfter.Delta!.Value.TotalSeconds.Should().BeInRange(1, 300);
    }

    [Fact]
    public void DiscordIngestBudget_IsTwentyPerTenMinutes_ByDefault()
    {
        var budget = new RateLimitSettings().DiscordIngest;

        budget.PermitLimit.Should().Be(20);
        budget.WindowSeconds.Should().Be(600);
    }

    [Fact]
    public void DiscordIngestBudget_IsReadFromApiRateLimitDiscordIngest()
    {
        var budget = factory.Services.GetRequiredService<IOptions<RateLimitSettings>>().Value.DiscordIngest;

        budget.PermitLimit.Should().Be(100000, "ApiFactory lifts the budget through the production environment prefix");
        budget.WindowSeconds.Should().Be(600);
    }

    [Fact]
    public void DiscordIngest_IsCountedPerKeyOwner_OrPerIpWhenAnonymous()
    {
        var owner = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "42")], DiscordIngestAuth.SchemeName)),
        };
        owner.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.60");
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.60");

        RateLimitingExtensions.DiscordIngestPartitionKey(owner).Should().Be("discord-ingest:user:42");
        RateLimitingExtensions.DiscordIngestPartitionKey(anonymous).Should().Be("discord-ingest:ip:203.0.113.60");
    }

    [Fact]
    public async Task DiscordIngestPolicy_SharesTheOwnersBudgetAcrossKeysAndAddresses()
    {
        // Exercise the registered policy before the real ingestion controller exists.
        // Authentication itself is covered by the dedicated scheme tests.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:RateLimit:DiscordIngest:PermitLimit"] = "2",
            ["Api:RateLimit:DiscordIngest:WindowSeconds"] = "600",
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddApiRateLimiting(configuration);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["Test-Peer"].ToString());
                    var owner = context.Request.Headers["Test-Owner"].ToString();
                    if (owner.Length > 0)
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, owner)], DiscordIngestAuth.SchemeName));
                    }
                    return next(context);
                });
                app.UseRateLimiter();
                app.UseEndpoints(endpoints => endpoints.MapPost("/ingest", context =>
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                }).RequireRateLimiting(RateLimitingExtensions.DiscordIngestPolicy));
            })).StartAsync();
        using var client = host.GetTestClient();

        async Task<HttpResponseMessage> SendAsync(string? owner, string peer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest");
            request.Headers.Add("Test-Peer", peer);
            if (owner is not null) request.Headers.Add("Test-Owner", owner);
            return await client.SendAsync(request);
        }

        (await SendAsync("42", "203.0.113.60")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync("42", "203.0.113.61")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rejected = await SendAsync("42", "203.0.113.62");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().Be(600);
        (await SendAsync("43", "203.0.113.60")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SendAsync(null, "203.0.113.70")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(null, "203.0.113.70")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(null, "203.0.113.70")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await SendAsync(null, "203.0.113.71")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
