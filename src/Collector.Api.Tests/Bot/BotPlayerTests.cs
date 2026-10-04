using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>/api/bot/players/{handle} and its history (spec § 5.2).</summary>
[Collection(ApiCollection.Name)]
public class BotPlayerTests(ApiFactory factory)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, string command)
    {
        var key = await CreateBotKeyAsync(factory);
        var response = await factory.CreateClient().SendAsync(Get(url, key, command));
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task APlayer_ShowsTheProfile_AndOnlyCurrentOrgs()
    {
        var handle = NewHandle();
        var (current, former) = (NewSid(), NewSid());
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, DisplayName = "The Pilot", Enlisted = new DateTime(2019, 9, 29, 0, 0, 0, DateTimeKind.Utc), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Organizations.Add(new Organization { Sid = current, Name = "Current Corp", Timestamp = DateTime.UtcNow, MembersCount = 3 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = current, UserHandle = handle, Timestamp = DateTime.UtcNow.AddDays(-40), IsActive = false, Rank = "Pilot", Stars = 1 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = current, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true, Rank = "Captain", Stars = 3 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = former, UserHandle = handle, Timestamp = DateTime.UtcNow.AddDays(-100), IsActive = false, Rank = "Recruit" });
        });

        var (status, body) = await GetAsync($"/api/bot/players/{handle.ToUpperInvariant()}", "joueur");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("handle").GetString().Should().Be(handle);
        body.GetProperty("citizenId").GetInt32().Should().Be(citizenId);
        body.GetProperty("profileRead").GetBoolean().Should().BeTrue();
        var orgs = body.GetProperty("currentOrgs").EnumerateArray().ToList();
        orgs.Should().ContainSingle();
        orgs[0].GetProperty("sid").GetString().Should().Be(current);
        orgs[0].GetProperty("rank").GetString().Should().Be("Captain");
        orgs[0].GetProperty("stars").GetInt32().Should().Be(3);
        orgs[0].GetProperty("since").GetString().Should().EndWith("Z");
        orgs[0].GetProperty("lastSeen").GetString().Should().EndWith("Z");
        orgs[0].GetProperty("since").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(-40), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ARosterOnlyPlayer_IsAPartialAnswer()
    {
        var handle = NewHandle();
        var sid = NewSid();
        await SeedAsync(factory, db => db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true, DisplayName = "Roster Name" }));

        var (status, body) = await GetAsync($"/api/bot/players/{handle}", "joueur");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("profileRead").GetBoolean().Should().BeFalse();
        body.GetProperty("displayName").GetString().Should().Be("Roster Name");
    }

    [Fact]
    public async Task AnUnknownPlayer_IsA404()
    {
        (await GetAsync($"/api/bot/players/{NewHandle()}", "joueur")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHistory_ListsEveryOrg_FormerHandles_AndRecentEvents()
    {
        var handle = NewHandle();
        var former = NewHandle();
        var (now, before) = (NewSid(), NewSid());
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-2) });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = handle, FirstSeen = DateTime.UtcNow.AddMonths(-2), LastSeen = DateTime.UtcNow });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = now, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = before, UserHandle = handle, Timestamp = DateTime.UtcNow.AddMonths(-3), IsActive = false });
            for (var i = 0; i < 20; i++)
                db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddMinutes(-i), EntityType = "member", EntityId = handle, ChangeType = "rank_changed", OrgSid = now, UserHandle = handle, OldValue = "a", NewValue = "b" });
        });

        var (status, body) = await GetAsync($"/api/bot/players/{handle}/history", "historique");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("orgs").EnumerateArray().Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("active").GetBoolean()))
            .Should().Equal((now, true), (before, false));
        body.GetProperty("handles").EnumerateArray().Select(h => h.GetProperty("handle").GetString())
            .Should().BeEquivalentTo(new[] { former, handle });
        body.GetProperty("events").GetArrayLength().Should().Be(15);
    }

    [Fact]
    public async Task AFormerHandle_FindsTheCurrentPlayer()
    {
        var handle = NewHandle();
        var former = NewHandle();
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-2) });
        });

        var (status, body) = await GetAsync($"/api/bot/players/{former}", "joueur");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("handle").GetString().Should().Be(handle);
    }

    [Fact]
    public async Task APlayerRequest_IsWrittenToTheActivityLog_UnderTheHandleAsTyped()
    {
        var handle = NewHandle();
        var typed = handle.ToUpperInvariant();
        await SeedAsync(factory, db => db.Users.Add(new User { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }));

        (await GetAsync($"/api/bot/players/{typed}", "joueur")).Status.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.EntityId == typed).ToListAsync();
        logs.Select(l => (l.Action, l.EntityType, l.EntityId))
            .Should().Equal(("bot:joueur", $"discord:{DiscordUser}", typed));
    }
}
