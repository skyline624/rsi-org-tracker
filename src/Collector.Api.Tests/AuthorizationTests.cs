using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Auth;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthorizationTests(ApiFactory factory)
{
    private static readonly HashSet<string> Anonymous =
    [
        "GET /", "GET /api/health", "GET /api/health/live", "GET /api/health/ready",
        "GET /api/auth/jwks", "POST /api/auth/login", "POST /api/auth/refresh", "POST /api/auth/logout",
    ];

    /// <summary>The only private route that an ingest-scoped key may reach once ingestion is implemented.</summary>
    private static readonly HashSet<string> IngestRoutes =
    [
        "POST /api/ingest/discord/guilds/x/syncs",
    ];

    private static string SamplePath(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart p when p.ParameterPolicies.Any(x => x.Content is "int" or "long") => "1",
            RoutePatternParameterPart => "x",
            _ => "",
        }))));

    /// <summary>Enumerates every route to prevent future endpoints from accidentally accepting restricted keys.</summary>
    private async Task<List<string>> RoutesNotAnswering401Async(HttpClient client, IReadOnlySet<string> skip)
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var open = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var path = SamplePath(endpoint.RoutePattern);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                var route = $"{method} {path}";
                if (skip.Contains(route)) continue;
                using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
                if (response.StatusCode != HttpStatusCode.Unauthorized) open.Add($"{route} -> {(int)response.StatusCode}");
            }
        }

        return open;
    }

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication_ExceptTheWhitelist()
    {
        using var client = factory.CreateClient();
        var open = await RoutesNotAnswering401Async(client, Anonymous);

        open.Should().BeEmpty("every non-whitelisted endpoint must answer 401 to an anonymous caller");
    }

    [Fact]
    public async Task DiscordIngestKey_IsRefusedEverywhere_ButTheIngestRoute()
    {
        using var owner = await factory.SignedInClientAsync($"authz-ingest-{Guid.NewGuid():N}");
        using var created = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = "walk",
            expiresAt = DateTime.UtcNow.AddDays(30),
            scope = DiscordIngestAuth.IngestScope,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rawKey = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);

        var open = await RoutesNotAnswering401Async(client, new HashSet<string>(Anonymous.Concat(IngestRoutes)));

        open.Should().BeEmpty("a discord:ingest key must authenticate nowhere but on the ingest route");
    }

    [Theory]
    [InlineData("register")]
    [InlineData("forgot-password")]
    [InlineData("reset-password")]
    public async Task DeadAccountFlows_AreGone(string route)
    {
        var admin = await factory.SignedInClientAsync($"admin-dead-{route}", isAdmin: true);

        var response = await admin.PostAsJsonAsync($"/api/auth/{route}", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminKeyInTheQueryString_IsIgnored()
    {
        var client = factory.CreateClient();

        (await client.GetAsync($"/api/admin/users?api_key={ApiFactory.AdminApiKey}"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("x-api-key", ApiFactory.AdminApiKey);
        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ManualOrganization_IsNormalisedAndValidated()
    {
        var client = await factory.SignedInClientAsync("org-author");

        var created = await client.PostAsJsonAsync("/api/admin/organizations", new { sid = "  newsid1 ", name = "New Org" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await created.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["sid"].ToString().Should().Be("NEWSID1");

        (await client.PostAsJsonAsync("/api/admin/organizations", new { sid = "newsid1", name = "Again" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/organizations", new { sid = "bad sid!", name = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/admin/organizations", new { sid = "WAYTOOLONGSID", name = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/admin/organizations", new { sid = "JSURL", name = "X", urlImage = "javascript:alert(1)" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ManualOrganization_CannotShadowACollectedOne()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Organizations.Add(new Organization
            {
                Sid = "COLLECTED", Name = "Real org", Timestamp = DateTime.UtcNow, MembersCount = 42,
                Source = OrganizationSource.Collected,
            });
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("org-shadow");

        (await client.PostAsJsonAsync("/api/admin/organizations", new { sid = "collected", name = "Fake" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CitizenId_OnceSet_CanOnlyBeChangedByAnAdmin()
    {
        var first = await factory.SignedInClientAsync("cid-first");
        var other = await factory.SignedInClientAsync("cid-other");
        var admin = await factory.SignedInClientAsync("cid-admin", isAdmin: true);

        (await first.PutAsJsonAsync("/api/users/redacted-pilot/citizen-id", new { citizenId = 1001 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.PutAsJsonAsync("/api/users/redacted-pilot/citizen-id", new { citizenId = 1002 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync("/api/users/redacted-pilot/citizen-id", new { citizenId = 1003 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Membership_CanOnlyBeRewrittenByItsAuthorOrAnAdmin()
    {
        var author = await factory.SignedInClientAsync("ms-author");
        var other = await factory.SignedInClientAsync("ms-other");
        var admin = await factory.SignedInClientAsync("ms-admin", isAdmin: true);
        await author.PostAsJsonAsync("/api/admin/organizations", new { sid = "MSORG", name = "Membership org" });
        var body = new { orgSid = "MSORG", rank = "Pilot" };

        (await author.PostAsJsonAsync("/api/users/ms-pilot/memberships", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.PostAsJsonAsync("/api/users/ms-pilot/memberships", body with { rank = "Spy" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await author.PostAsJsonAsync("/api/users/ms-pilot/memberships", body with { rank = "Captain" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("/api/users/ms-pilot/memberships", body with { rank = "Admiral" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
