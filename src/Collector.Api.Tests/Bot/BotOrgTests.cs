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

/// <summary>/api/bot/orgs/{sid}, its members and its movements (spec § 5.2, § 5.5).</summary>
[Collection(ApiCollection.Name)]
public class BotOrgTests(ApiFactory factory)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, string command)
    {
        var key = await CreateBotKeyAsync(factory);
        var response = await factory.CreateClient().SendAsync(Get(url, key, command));
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task AnOrg_ShowsItsListing_Counts_AndTrend()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Counted Corp", Timestamp = DateTime.UtcNow, MembersCount = 40, Archetype = "PMC", Lang = "French" });
            db.OrgMemberCounts.Add(new OrgMemberCount { OrgSid = sid, CollectedAt = DateTime.UtcNow.AddDays(-45), TotalRows = 30, VisibleCount = 25, RedactedCount = 3, HiddenCount = 2 });
            db.OrgMemberCounts.Add(new OrgMemberCount { OrgSid = sid, CollectedAt = DateTime.UtcNow.AddDays(-1), TotalRows = 40, VisibleCount = 34, RedactedCount = 4, HiddenCount = 2 });
        });

        var (status, body) = await GetAsync($"/api/bot/orgs/{sid}", "org");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("name").GetString().Should().Be("Counted Corp");
        var counts = body.GetProperty("counts");
        (counts.GetProperty("total").GetInt32(), counts.GetProperty("visible").GetInt32(),
            counts.GetProperty("redacted").GetInt32(), counts.GetProperty("hidden").GetInt32())
            .Should().Be((40, 34, 4, 2));
        var trend = body.GetProperty("trend30d");
        (trend.GetProperty("from").GetInt32(), trend.GetProperty("to").GetInt32()).Should().Be((30, 40));
    }

    [Fact]
    public async Task AnOrgSid_IsNormalized()
    {
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Normalized", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/%20{sid.ToLowerInvariant()}%20", "org")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnUnknownOrg_IsA404_OnEveryRoute()
    {
        var sid = NewSid();
        (await GetAsync($"/api/bot/orgs/{sid}", "org")).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/api/bot/orgs/{sid}/members", "membres")).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/api/bot/orgs/{sid}/movements", "mouvements")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_AreTheActiveOnes_ByPagesOf25_AndMaskedRowsAreNeverListed()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Paged", Timestamp = DateTime.UtcNow, MembersCount = 32 });
            for (var i = 0; i < 30; i++)
                db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = $"member{i:00}", Timestamp = DateTime.UtcNow, IsActive = true, Stars = 2 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = "gone", Timestamp = DateTime.UtcNow.AddDays(-9), IsActive = false });
            // Two masked rows are counted only: org_member_counts carries them, rosters never do.
            db.OrgMemberCounts.Add(new OrgMemberCount { OrgSid = sid, CollectedAt = DateTime.UtcNow, TotalRows = 32, VisibleCount = 30, RedactedCount = 1, HiddenCount = 1 });
        });

        var (_, first) = await GetAsync($"/api/bot/orgs/{sid}/members", "membres");
        var (_, second) = await GetAsync($"/api/bot/orgs/{sid}/members?page=2", "membres");

        first.GetProperty("total").GetInt32().Should().Be(30);
        first.GetProperty("items").GetArrayLength().Should().Be(25);
        second.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("handle").GetString())
            .Should().Equal("member25", "member26", "member27", "member28", "member29");
    }

    [Fact]
    public async Task Movements_AreTheJoinsAndLeavesOfTheLastDays()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Moving", Timestamp = DateTime.UtcNow, MembersCount = 2 });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-2), EntityType = "member", EntityId = "newbie", ChangeType = "member_joined", OrgSid = sid, UserHandle = "newbie" });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-3), EntityType = "member", EntityId = "leaver", ChangeType = "member_left", OrgSid = sid, UserHandle = "leaver" });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-20), EntityType = "member", EntityId = "old", ChangeType = "member_left", OrgSid = sid, UserHandle = "old" });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-1), EntityType = "member", EntityId = "ranked", ChangeType = "rank_changed", OrgSid = sid, UserHandle = "ranked" });
        });

        var (status, body) = await GetAsync($"/api/bot/orgs/{sid}/movements?days=7", "mouvements");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("joined").EnumerateArray().Select(m => m.GetProperty("handle").GetString()).Should().Equal("newbie");
        body.GetProperty("left").EnumerateArray().Select(m => m.GetProperty("handle").GetString()).Should().Equal("leaver");
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task MovementDaysOutsideTheirBounds_AreA400(int days)
    {
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Bounds", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/{sid}/movements?days={days}", "mouvements")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AMembersPageBelowOne_IsA400(int page)
    {
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Pages", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/{sid}/members?page={page}", "membres")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnOrgRequest_IsWrittenToTheActivityLog_UnderTheSidAsTyped()
    {
        var sid = NewSid();
        var typed = sid.ToLowerInvariant();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Logged", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/{typed}", "org")).Status.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.EntityId == typed).ToListAsync();
        logs.Select(l => (l.Action, l.EntityType, l.EntityId))
            .Should().Equal(("bot:org", $"discord:{DiscordUser}", typed));
    }
}
