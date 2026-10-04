using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Models;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>Who may call /api/bot, the headers it requires, and what it writes to the activity log (spec § 5.1, § 5.3).</summary>
[Collection(ApiCollection.Name)]
public class BotAccessTests(ApiFactory factory)
{
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => await factory.CreateClient().SendAsync(request);

    [Fact]
    public async Task ABotKey_WithItsHeaders_CanSearch()
    {
        var key = await CreateBotKeyAsync(factory);
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Searchable Corp", Timestamp = DateTime.UtcNow, MembersCount = 12 }));

        var response = await SendAsync(Get($"/api/bot/search?q={sid}", key, "recherche"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("orgs").EnumerateArray().Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("membersCount").GetInt32()))
            .Should().Contain((sid, 12));
    }

    [Fact]
    public async Task EveryOtherCredential_IsRefused()
    {
        var owner = await factory.SignedInClientAsync($"bot-other-{Guid.NewGuid():N}");
        var fullKey = (await (await owner.PostAsJsonAsync("/api/api-keys", new { name = "full" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();
        var ingestKey = (await (await owner.PostAsJsonAsync("/api/api-keys",
                new { name = "ingest", expiresAt = DateTime.UtcNow.AddDays(30), scope = ApiKeyScopes.DiscordIngest }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();

        foreach (var key in new[] { fullKey, ingestKey, ApiFactory.AdminApiKey, null })
        {
            (await SendAsync(Get("/api/bot/search?q=ab", key, "recherche"))).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, $"key {key?[..6]}");
        }
        (await owner.SendAsync(Get("/api/bot/search?q=ab", null, "recherche"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a signed-in session has no standing on /api/bot");
    }

    [Fact]
    public async Task ABotKey_IsWorthNothing_OutsideTheBotRoutes()
    {
        var key = await CreateBotKeyAsync(factory);

        foreach (var url in new[] { "/api/organizations", "/api/users?search=ab", "/api/changes" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", key);
            (await SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
        }
    }

    [Theory]
    [InlineData(null, "recherche")]
    [InlineData("not-a-snowflake", "recherche")]
    [InlineData(DiscordUser, "psyche")]
    [InlineData(DiscordUser, "")]
    public async Task MissingOrInvalidHeaders_AreA400(string? discordUser, string command)
    {
        var key = await CreateBotKeyAsync(factory);

        (await SendAsync(Get("/api/bot/search?q=ab", key, command, discordUser))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/api/bot/players/{0}")]
    [InlineData("/api/bot/players/{0}/history")]
    [InlineData("/api/bot/orgs/{0}")]
    [InlineData("/api/bot/orgs/{0}/members")]
    [InlineData("/api/bot/orgs/{0}/movements")]
    public async Task Autocomplete_IsAcceptedOnTheSearchOnly(string route)
    {
        // Autocompletion is never logged: any other route would let reads escape the activity log.
        var key = await CreateBotKeyAsync(factory);
        var sid = NewSid();
        var handle = NewHandle();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Autocomplete Only", Timestamp = DateTime.UtcNow, MembersCount = 1 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true });
        });
        var url = string.Format(route, route.Contains("players") ? handle : sid);

        (await SendAsync(Get(url, key, "autocomplete"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, url);
        (await SendAsync(Get($"/api/bot/search?q={sid}", key, "autocomplete"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ASearch_IsWrittenToTheActivityLog_AnAutocompleteIsNot()
    {
        var key = await CreateBotKeyAsync(factory);
        var text = "zz" + Guid.NewGuid().ToString("N")[..6];

        (await SendAsync(Get($"/api/bot/search?q={text}", key, "recherche"))).EnsureSuccessStatusCode();
        (await SendAsync(Get($"/api/bot/search?q={text}x", key, "autocomplete"))).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.EntityId != null && l.EntityId.StartsWith(text)).ToListAsync();
        logs.Select(l => (l.Action, l.EntityType, l.EntityId))
            .Should().Equal(("bot:recherche", $"discord:{DiscordUser}", text));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("012345678901234567890123456789012345678901234567890")]
    public async Task ASearchOutsideItsBounds_IsA400(string q)
    {
        var key = await CreateBotKeyAsync(factory);

        (await SendAsync(Get($"/api/bot/search?q={q}", key, "recherche"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
