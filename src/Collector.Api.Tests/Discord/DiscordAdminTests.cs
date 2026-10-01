using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Dtos.Discord;
using Collector.Api.Models;
using Collector.Api.Services.Discord;
using Collector.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Discord.DiscordTestKit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Out-of-band admin erasure and exclusion, as seen from the next sync. Each write is audited, and the static key is audited
/// without a user.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordAdminTests(ApiFactory factory)
{
    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-400);

    [Theory]
    [InlineData("DELETE", "accounts/{id}")]
    [InlineData("DELETE", "guilds/{id}?exclude=false")]
    [InlineData("GET", "optouts")]
    [InlineData("DELETE", "optouts/{id}")]
    [InlineData("GET", "guild-optouts")]
    [InlineData("DELETE", "guild-optouts/{id}")]
    public async Task NonAdmins_AreRefused(string method, string route)
    {
        var path = "/api/discord/" + route.Replace("{id}", NewSnowflake());
        var user = await factory.SignedInClientAsync($"dadm-user-{NewSnowflake()}");
        var (ingest, _, _) = await IngestClientAsync(factory, $"dadm-key-{NewSnowflake()}");

        (await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ingest.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a discord:ingest key is authenticated nowhere else");
    }

    [Fact]
    public async Task EraseAccount_RemovesItsRows_AndLaterSyncsIgnoreIt()
    {
        var (admin, adminId) = await AdminAsync("dadm-erase-account");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-erase-account-key");
        var guild = NewSnowflake();
        var (erased, kept) = (NewSnowflake(), NewSnowflake());
        var members = new[] { Member(erased, "erased", nick: "Erased", joinedAt: LongAgo), Member(kept, "kept", joinedAt: LongAgo) };
        await PostOkAsync(ingest, guild, Sync(guild, members, durationMs: 0));
        await PostOkAsync(ingest, guild, Sync(guild, [members[0] with { Nick = "Renamed" }, members[1]], durationMs: 0));

        (await admin.DeleteAsync($"/api/discord/accounts/{erased}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TrackerAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == erased))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == erased))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.DiscordUserId == erased))).Should().BeFalse();
        var optOut = await TrackerAsync(db => db.DiscordOptOuts.AsNoTracking().SingleAsync(o => o.DiscordUserId == erased));
        optOut.ByApiUserId.Should().Be(adminId);
        optOut.ByUsername.Should().Be("dadm-erase-account");
        (await AuditAsync(DiscordAudit.EraseAccount, erased)).Should().ContainSingle().Which.ApiUserId.Should().Be(adminId);

        var again = await PostOkAsync(ingest, guild, Sync(guild, members, durationMs: 0));

        again.GetProperty("membersOptedOut").GetInt32().Should().Be(1);
        again.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == erased)))
            .Should().BeFalse("an erased account stays out of every later sync");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/discord/optouts");
        list.EnumerateArray().Should().Contain(o =>
            o.GetProperty("discordUserId").GetString() == erased && o.GetProperty("byUsername").GetString() == "dadm-erase-account");
    }

    [Fact]
    public async Task EraseAccount_DuringAnIngestion_IsNeverUndoneByIt()
    {
        // Spec § 15 RGPD. Both writes take the Discord write gate, so whichever runs second sees
        // the other's commit: the ingestion skips an opted-out account, or the erasure removes
        // what the ingestion wrote. Either order ends with the account erased and opted out.
        var (admin, _) = await AdminAsync("dadm-erase-race");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-erase-race-key");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var body = Sync(guild,
            [Member(user, "racing", joinedAt: LongAgo), Member(NewSnowflake(), "bystander", joinedAt: LongAgo)], durationMs: 0);
        await PostOkAsync(ingest, guild, body);
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();
        Task<HttpResponseMessage> erase;
        Task<HttpResponseMessage> repost;

        // Plays an ingestion in progress: both requests queue behind it.
        using (await gate.EnterAsync(CancellationToken.None))
        {
            erase = admin.DeleteAsync($"/api/discord/accounts/{user}");
            repost = PostSyncAsync(ingest, guild, body);
        }

        using (var erased = await erase)
        {
            erased.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var reposted = await repost)
        {
            reposted.StatusCode.Should().Be(HttpStatusCode.OK, await reposted.Content.ReadAsStringAsync());
        }
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == user))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == user))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordOptOuts.AnyAsync(o => o.DiscordUserId == user))).Should().BeTrue();
    }

    [Fact]
    public async Task EraseGuild_WithExclusion_RefusesLaterSyncs_UntilTheExclusionIsLifted()
    {
        var (admin, adminId) = await AdminAsync("dadm-exclude");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-exclude-key");
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "excluded", joinedAt: LongAgo)], durationMs: 0);
        await PostOkAsync(ingest, guild, body);

        (await admin.DeleteAsync($"/api/discord/guilds/{guild}?exclude=true")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TrackerAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordMembers.AnyAsync(m => m.GuildId == guild))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordSyncs.AnyAsync(s => s.GuildId == guild))).Should().BeFalse();
        (await AuditAsync(DiscordAudit.EraseGuild, guild)).Should().ContainSingle().Which.ApiUserId.Should().Be(adminId);
        using (var refused = await PostSyncAsync(ingest, guild, body))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("guild_excluded");
        }
        var excluded = await admin.GetFromJsonAsync<JsonElement>("/api/discord/guild-optouts");
        excluded.EnumerateArray().Should().Contain(o => o.GetProperty("guildId").GetString() == guild);

        (await admin.DeleteAsync($"/api/discord/guild-optouts/{guild}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AuditAsync(DiscordAudit.RemoveGuildOptOut, guild)).Should().ContainSingle();
        (await PostOkAsync(ingest, guild, body)).GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        (await admin.DeleteAsync($"/api/discord/guild-optouts/{guild}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EraseGuild_WithoutExclusion_TheNextSyncIsANewBaseline()
    {
        var (admin, _) = await AdminAsync("dadm-restart");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-restart-key");
        var guild = NewSnowflake();
        var (stays, leaves) = (NewSnowflake(), NewSnowflake());
        await PostOkAsync(ingest, guild, Sync(guild,
            [Member(stays, "stays", joinedAt: LongAgo), Member(leaves, "leaves", joinedAt: LongAgo)], durationMs: 0));
        await PostOkAsync(ingest, guild, Sync(guild, [Member(stays, "stays", joinedAt: LongAgo)], durationMs: 0));

        (await admin.DeleteAsync($"/api/discord/guilds/{guild}?exclude=false")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TrackerAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.GuildId == guild))).Should().BeFalse();
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == stays || a.DiscordUserId == leaves)))
            .Should().BeFalse("unlinked accounts left without any member row go with the guild");
        (await TrackerAsync(db => db.DiscordGuildOptOuts.AnyAsync(o => o.GuildId == guild))).Should().BeFalse();

        var restarted = await PostOkAsync(ingest, guild, Sync(guild, [Member(stays, "stays", joinedAt: LongAgo)], durationMs: 0));

        restarted.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EraseGuild_OfAnUnknownGuild_Is404_UnlessItIsBeingExcluded()
    {
        var (admin, _) = await AdminAsync("dadm-unknown-guild");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-unknown-guild-key");
        var unknown = NewSnowflake();

        (await admin.DeleteAsync($"/api/discord/guilds/{unknown}?exclude=false")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditAsync(DiscordAudit.EraseGuild, unknown)).Should().BeEmpty();

        (await admin.DeleteAsync($"/api/discord/guilds/{unknown}?exclude=true")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var refused = await PostSyncAsync(ingest, unknown,
            Sync(unknown, [Member(NewSnowflake(), "early", joinedAt: LongAgo)], durationMs: 0));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "a guild can be excluded before its first sync");
    }

    [Theory]
    [InlineData("DELETE", "guilds/{id}")]
    [InlineData("DELETE", "guilds/not-a-snowflake?exclude=false")]
    [InlineData("DELETE", "accounts/12345")]
    [InlineData("DELETE", "optouts/abc")]
    [InlineData("DELETE", "guild-optouts/abc")]
    public async Task MalformedRequests_Are400(string method, string route)
    {
        var (admin, _) = await AdminAsync($"dadm-bad-{NewSnowflake()}");
        var path = "/api/discord/" + route.Replace("{id}", NewSnowflake());

        (await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemovingAnAccountOptOut_LetsLaterSyncsRecordItAgain()
    {
        var (admin, _) = await AdminAsync("dadm-optout");
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-optout-key");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var body = Sync(guild, [Member(user, "comeback", joinedAt: LongAgo)], durationMs: 0);
        await PostOkAsync(ingest, guild, body);
        (await admin.DeleteAsync($"/api/discord/accounts/{user}")).EnsureSuccessStatusCode();

        (await admin.DeleteAsync($"/api/discord/optouts/{user}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AuditAsync(DiscordAudit.RemoveOptOut, user)).Should().ContainSingle();
        (await admin.DeleteAsync($"/api/discord/optouts/{user}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var again = await PostOkAsync(ingest, guild, body);
        again.GetProperty("membersOptedOut").GetInt32().Should().Be(0);
        (await TrackerAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == user))).Should().BeTrue();
    }

    [Fact]
    public async Task TheManualMassDepartureAuthorizationRoute_NoLongerExists()
    {
        var (admin, _) = await AdminAsync("dadm-removed-mass-route");
        (await admin.PostAsync($"/api/discord/guilds/{NewSnowflake()}/allow-mass-departure", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StaticAdminKey_CanAct_AndIsAuditedWithoutAUser()
    {
        var (ingest, _, _) = await IngestClientAsync(factory, "dadm-static-key");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        await PostOkAsync(ingest, guild, Sync(guild,
            [Member(user, "static", joinedAt: LongAgo), Member(NewSnowflake(), "other", joinedAt: LongAgo)], durationMs: 0));
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("x-api-key", ApiFactory.AdminApiKey);

        (await admin.DeleteAsync($"/api/discord/accounts/{user}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var optOut = await TrackerAsync(db => db.DiscordOptOuts.AsNoTracking().SingleAsync(o => o.DiscordUserId == user));
        optOut.ByApiUserId.Should().BeNull();
        optOut.ByUsername.Should().Be("admin");
        (await AuditAsync(DiscordAudit.EraseAccount, user)).Should().ContainSingle().Which.ApiUserId.Should().BeNull();
    }

    // --- helpers ---

    private async Task<(HttpClient Client, long UserId)> AdminAsync(string username)
    {
        var client = await factory.SignedInClientAsync(username, isAdmin: true);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        return (client, me.GetProperty("id").GetInt64());
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string guildId, DiscordSyncRequest body)
    {
        using var response = await PostSyncAsync(client, guildId, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> TrackerAsync<T>(Func<TrackerDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private async Task<List<ActivityLog>> AuditAsync(string action, string entityId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.Action == action && l.EntityId == entityId)
            .ToListAsync();
    }
}
