using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Models;
using FluentAssertions;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// GET api/discord/guilds, …/guilds/{guildId}, …/members, …/events and …/syncs (spec § 11):
/// guild summaries and detail, members with rank, links and status, the guild's history and
/// its sync log.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordRosterQueryTests(ApiFactory factory)
{
    private static int _seq = 73_000;
    private readonly DiscordReadSeed _seed = new(factory);

    private static DateTime At => DiscordReadSeed.At;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_730_000_000 + n * 20 + k;

    [Fact]
    public async Task PendingReceipts_AreHiddenWhileEventsOutliveExpiredReceipts()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var userId = await _seed.SeedMemberAsync(guildId, $"pending-{n}");
        var handle = $"PendingPerson{n}";
        await _seed.SeedPersonAsync(null, handle, null, userId);
        var accepted = await SeedSyncAsync(guildId, "accepted", At);
        var pending = await SeedSyncAsync(guildId, "pending", At.AddDays(1));
        await _seed.WithDbAsync(async db =>
        {
            var row = await db.DiscordSyncs.FindAsync(pending);
            row!.EventCount = -1;
            await db.SaveChangesAsync();
        });
        var first = await SeedEventAsync(guildId, userId, accepted, DiscordEventTypes.NickChanged, "old", "visible");
        await SeedEventAsync(guildId, userId, pending, DiscordEventTypes.NickChanged, "visible", "pending");
        var orphan = await SeedEventAsync(guildId, userId, long.MaxValue, DiscordEventTypes.NickChanged, "old", "retained");
        var client = await factory.SignedInClientAsync($"c3-pending-{n}");

        Ids(await GetOkAsync(client, $"/api/discord/guilds/{guildId}/syncs")).Should().Equal(accepted);
        Ids(await GetOkAsync(client, $"/api/discord/guilds/{guildId}/events")).Should().Equal(orphan, first);
        var guild = await GetOkAsync(client, $"/api/discord/guilds/{guildId}");
        guild.GetProperty("lastSync").GetProperty("submittedBy").GetString().Should().Be("accepted");
        var profile = await GetOkAsync(client, $"/api/users/{handle}/discord");
        profile.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("newValue").GetString())
            .Should().BeEquivalentTo(["visible", "retained"]);
    }

    [Fact]
    public async Task APageBeyondIntegerOffsets_ReturnsAnEmptyPage()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var rank = await _seed.SeedRoleAsync(guildId, "Rank", 1, isRank: true);
        await _seed.SeedMemberAsync(guildId, $"huge-page-{n}", [rank]);
        var client = await factory.SignedInClientAsync($"c3-huge-page-{n}");

        foreach (var filter in new[] { "", $"&rankRoleId={rank}" })
        {
            var page = await GetOkAsync(client,
                $"/api/discord/guilds/{guildId}/members?page={int.MaxValue}&pageSize=200{filter}");
            Items(page).Should().BeEmpty();
            page.GetProperty("total").GetInt32().Should().Be(1);
        }
    }

    [Fact]
    public async Task TheGuildList_PutsUnmappedGuildsFirst_ThenSortsByName_AndSummarisesEachGuild()
    {
        var n = Next();
        var sid = $"QGL{n}";
        await _seed.SeedOrgAsync(sid, "Corpo Q");
        var alpha = await _seed.SeedGuildAsync(sid, name: $"alpha {n}", mappedByApiUserId: 1, mappedByUsername: $"c3-owner-{n}");
        var mike = await _seed.SeedGuildAsync(null, name: $"Mike {n}");
        var bravo = await _seed.SeedGuildAsync(null, name: $"bravo {n}", complete: false);
        var zulu = await _seed.SeedGuildAsync(sid, name: $"Zulu {n}");
        var officier = await _seed.SeedRoleAsync(alpha, "Officier", 20, isRank: true, color: "#ff0000");
        var recrue = await _seed.SeedRoleAsync(alpha, "Recrue", 10, isRank: true, color: null);
        var pilote = await _seed.SeedRoleAsync(alpha, "Pilote", 30, isRank: false);
        await _seed.SeedMemberAsync(alpha, $"q-officer{n}", [officier, recrue, pilote]);
        await _seed.SeedMemberAsync(alpha, $"q-recruit{n}", [recrue]);
        await _seed.SeedMemberAsync(alpha, $"q-recruit2{n}", [recrue]);
        await _seed.SeedMemberAsync(alpha, $"q-plain{n}", [pilote]);
        await _seed.SeedMemberAsync(alpha, $"q-bot{n}", [officier], bot: true);
        await _seed.SeedMemberAsync(alpha, $"q-left{n}", [officier], left: true);
        await SeedSyncAsync(alpha, $"first-{n}", At.AddDays(-2));
        await SeedSyncAsync(alpha, $"last-{n}", At.AddDays(-1), isComplete: false, guard: true, method: DiscordSyncMethods.RoleMembers);
        var client = await factory.SignedInClientAsync($"c3-list-{n}");

        var guilds = (await GetOkAsync(client, "/api/discord/guilds")).EnumerateArray().ToList();

        var mine = new[] { alpha, mike, bravo, zulu };
        guilds.Select(g => g.GetProperty("guildId").GetString()).Where(id => mine.Contains(id))
            .Should().Equal(bravo, mike, alpha, zulu);
        var summary = guilds.Single(g => g.GetProperty("guildId").GetString() == alpha);
        summary.GetProperty("name").GetString().Should().Be($"alpha {n}");
        summary.GetProperty("orgSid").GetString().Should().Be(sid);
        summary.GetProperty("orgName").GetString().Should().Be("Corpo Q");
        summary.GetProperty("orgMappedBy").GetString().Should().Be($"c3-owner-{n}");
        summary.GetProperty("activeMembers").GetInt32().Should().Be(4, "bots and departed members are not counted");
        summary.GetProperty("rankDistribution").EnumerateArray()
            .Select(r => (r.GetProperty("roleId").GetString(), r.GetProperty("name").GetString(),
                r.GetProperty("color").GetString(), r.GetProperty("count").GetInt32()))
            .Should().Equal((officier, "Officier", "#ff0000", 1), (recrue, "Recrue", null, 2));
        var lastSync = summary.GetProperty("lastSync");
        lastSync.GetProperty("submittedBy").GetString().Should().Be($"last-{n}");
        lastSync.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        lastSync.GetProperty("method").GetString().Should().Be("role-members");
        lastSync.GetProperty("massDepartureDetected").GetBoolean().Should().BeTrue();
        lastSync.GetProperty("receivedAt").GetDateTime().Should().Be(At.AddDays(-1));
        summary.GetProperty("lastCompleteSyncAt").GetDateTime().Should().Be(At);
        var bravoSummary = guilds.Single(g => g.GetProperty("guildId").GetString() == bravo);
        bravoSummary.GetProperty("lastSync").ValueKind.Should().Be(JsonValueKind.Null);
        bravoSummary.GetProperty("lastCompleteSyncAt").ValueKind.Should().Be(JsonValueKind.Null);
        bravoSummary.GetProperty("orgName").ValueKind.Should().Be(JsonValueKind.Null);
        bravoSummary.GetProperty("rankDistribution").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AGuildsDetail_ListsEveryRoleWithItsMembers_TheOrgsRsiRanks_AndWhoMayEditIt()
    {
        var n = Next();
        var sid = $"QGD{n}";
        await _seed.SeedOrgAsync(sid, "Corpo D");
        await _seed.SeedRosterAsync(sid, $"R1{n}", Cid(n, 1), " Officer", stars: 4);
        await _seed.SeedRosterAsync(sid, $"R2{n}", Cid(n, 2), "officer ", stars: 4);
        await _seed.SeedRosterAsync(sid, $"R3{n}", Cid(n, 3), "Recruit", stars: 1);
        await _seed.SeedRosterAsync(sid, $"R4{n}", Cid(n, 4), null);
        await _seed.SeedRosterAsync(sid, $"R5{n}", Cid(n, 5), "Legacy", active: false, stars: 5);
        var owner = await factory.SignedInClientAsync($"c3-owner-{n}");
        var ownerId = (await GetOkAsync(owner, "/api/auth/me")).GetProperty("id").GetInt64();
        var mapped = await _seed.SeedGuildAsync(sid, mappedByApiUserId: ownerId, mappedByUsername: $"c3-owner-{n}");
        var officier = await _seed.SeedRoleAsync(mapped, "Officier", 20, isRank: true, rankOrder: 5, rsiRankLabel: "Officer");
        var recrue = await _seed.SeedRoleAsync(mapped, "Recrue", 10, isRank: true);
        var pilote = await _seed.SeedRoleAsync(mapped, "Pilote", 30, isRank: false, color: null);
        var ancien = await _seed.SeedRoleAsync(mapped, "Ancien", 40, isRank: true, deleted: true);
        await _seed.SeedMemberAsync(mapped, $"d-one{n}", [officier, pilote]);
        await _seed.SeedMemberAsync(mapped, $"d-two{n}", [recrue, pilote, ancien]);
        await _seed.SeedMemberAsync(mapped, $"d-bot{n}", [pilote], bot: true);
        await _seed.SeedMemberAsync(mapped, $"d-left{n}", [officier], left: true);
        var unmapped = await _seed.SeedGuildAsync(null);
        var other = await factory.SignedInClientAsync($"c3-other-{n}");
        var admin = await factory.SignedInClientAsync($"c3-admin-{n}", isAdmin: true);
        var url = $"/api/discord/guilds/{mapped}";

        var detail = await GetOkAsync(other, url);

        detail.GetProperty("roles").EnumerateArray()
            .Select(r => (r.GetProperty("roleId").GetString(), r.GetProperty("name").GetString(), r.GetProperty("position").GetInt32(),
                r.GetProperty("isRank").GetBoolean(), Int(r.GetProperty("rankOrder")), r.GetProperty("rsiRankLabel").GetString(),
                r.GetProperty("deleted").GetBoolean(), r.GetProperty("memberCount").GetInt32()))
            .Should().Equal(
                (pilote, "Pilote", 30, false, null, null, false, 2),
                (officier, "Officier", 20, true, 5, "Officer", false, 1),
                (recrue, "Recrue", 10, true, 10, null, false, 1),
                (ancien, "Ancien", 40, true, 40, null, true, 1));
        // RankOrder decides before Position: Recrue (10) ranks above Officier (5).
        detail.GetProperty("rankDistribution").EnumerateArray().Select(r => r.GetProperty("roleId").GetString())
            .Should().Equal(recrue, officier);
        detail.GetProperty("rsiRanks").EnumerateArray().Select(r => r.GetString()).Should().Equal("Officer", "Recruit");
        detail.GetProperty("orgName").GetString().Should().Be("Corpo D");
        detail.GetProperty("canEdit").GetBoolean().Should().BeFalse("another user mapped this guild");
        (await GetOkAsync(owner, url)).GetProperty("canEdit").GetBoolean().Should().BeTrue();
        (await GetOkAsync(admin, url)).GetProperty("canEdit").GetBoolean().Should().BeTrue();
        var unmappedDetail = await GetOkAsync(other, $"/api/discord/guilds/{unmapped}");
        unmappedDetail.GetProperty("canEdit").GetBoolean().Should().BeTrue("anyone may configure an unmapped guild");
        unmappedDetail.GetProperty("rsiRanks").GetArrayLength().Should().Be(0);
        unmappedDetail.GetProperty("roles").GetArrayLength().Should().Be(0);
        (await other.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_AreFilteredByStatusAndName_AndPagedInNameOrder()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var anna = await _seed.SeedMemberAsync(guildId, $"anna{n}", nick: "Zed");
        var bob = await _seed.SeedMemberAsync(guildId, $"bob{n}", globalName: "Bobby");
        await _seed.SeedMemberAsync(guildId, $"carl{n}");
        var dora = await _seed.SeedMemberAsync(guildId, $"dora{n}", left: true);
        var echo = await _seed.SeedMemberAsync(guildId, $"echo{n}", bot: true);
        var client = await factory.SignedInClientAsync($"c3-members-{n}");
        var url = $"/api/discord/guilds/{guildId}/members";

        var active = await GetOkAsync(client, url);

        // Sorted by nick, else global name, else username, regardless of case.
        Usernames(active).Should().Equal($"bob{n}", $"carl{n}", $"echo{n}", $"anna{n}");
        active.GetProperty("total").GetInt32().Should().Be(4);
        Items(active).Should().OnlyContain(m => m.GetProperty("reconciliation").ValueKind == JsonValueKind.Null);
        Items(active).Single(m => m.GetProperty("discordUserId").GetString() == echo).GetProperty("isBot").GetBoolean().Should().BeTrue();
        Items(active).Single(m => m.GetProperty("discordUserId").GetString() == bob).GetProperty("globalName").GetString().Should().Be("Bobby");
        Items(active).Single(m => m.GetProperty("discordUserId").GetString() == anna).GetProperty("nick").GetString().Should().Be("Zed");

        var second = await GetOkAsync(client, $"{url}?status=active&page=2&pageSize=2");
        Usernames(second).Should().Equal($"echo{n}", $"anna{n}");
        second.GetProperty("total").GetInt32().Should().Be(4);
        second.GetProperty("page").GetInt32().Should().Be(2);
        second.GetProperty("pageSize").GetInt32().Should().Be(2);

        var former = await GetOkAsync(client, $"{url}?status=former");
        Usernames(former).Should().Equal($"dora{n}");
        Items(former).Single().GetProperty("leftAt").GetDateTime().Should().Be(At);
        Usernames(await GetOkAsync(client, $"{url}?status=all"))
            .Should().Equal($"bob{n}", $"carl{n}", $"dora{n}", $"echo{n}", $"anna{n}");
        Items(former).Single().GetProperty("discordUserId").GetString().Should().Be(dora);

        Usernames(await GetOkAsync(client, $"{url}?search=BOBBY")).Should().Equal($"bob{n}");
        Usernames(await GetOkAsync(client, $"{url}?search=zed")).Should().Equal($"anna{n}");
        Usernames(await GetOkAsync(client, $"{url}?search=CARL{n}")).Should().Equal($"carl{n}");
        Usernames(await GetOkAsync(client, $"{url}?status=all&search=dora")).Should().Equal($"dora{n}");
        (await GetOkAsync(client, $"{url}?search=%25")).GetProperty("total").GetInt32().Should().Be(0, "wildcards are escaped");

        // An unmapped guild has no status: the filter is ignored.
        Usernames(await GetOkAsync(client, $"{url}?reconciliation=ok")).Should().HaveCount(4);

        (await client.GetAsync($"{url}?status=everyone")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/members")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_CarryTheirRankRolesLinksAndStatus_AndAreFilteredByRankAndStatus()
    {
        var n = Next();
        var sid = $"QGM{n}";
        await _seed.SeedOrgAsync(sid, $"Org {sid}");
        await _seed.SeedRosterAsync(sid, $"Ok{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sid, $"Low{n}", Cid(n, 2), "Recruit");
        var guildId = await _seed.SeedGuildAsync(sid);
        var officier = await _seed.SeedRoleAsync(guildId, "Officier", 20, isRank: true, rsiRankLabel: "Officer", color: "#ff0000");
        var recrue = await _seed.SeedRoleAsync(guildId, "Recrue", 10, isRank: true, rsiRankLabel: "Recruit", color: null);
        var pilote = await _seed.SeedRoleAsync(guildId, "Pilote", 30, isRank: false, color: null);
        var ok = await _seed.SeedMemberAsync(guildId, $"m-ok{n}", [recrue, officier, pilote]);
        var mismatch = await _seed.SeedMemberAsync(guildId, $"m-mismatch{n}", [officier]);
        var outsider = await _seed.SeedMemberAsync(guildId, $"m-outsider{n}", [recrue]);
        var unlinked = await _seed.SeedMemberAsync(guildId, $"m-unlinked{n}", [recrue]);
        var bot = await _seed.SeedMemberAsync(guildId, $"m-zbot{n}", [officier], bot: true);
        await _seed.SeedPersonAsync(Cid(n, 1), $"Ok{n}", "Okay", ok);
        await _seed.SeedPersonAsync(Cid(n, 2), $"Low{n}", null, mismatch);
        await _seed.SeedPersonAsync(Cid(n, 3), $"Out{n}", null, outsider);
        await _seed.SeedPersonAsync(Cid(n, 4), $"OutToo{n}", null, outsider);
        var client = await factory.SignedInClientAsync($"c3-status-{n}");
        var url = $"/api/discord/guilds/{guildId}/members";

        var all = await GetOkAsync(client, url);

        Usernames(all).Should().Equal($"m-mismatch{n}", $"m-ok{n}", $"m-outsider{n}", $"m-unlinked{n}", $"m-zbot{n}");
        var byId = Items(all).ToDictionary(m => m.GetProperty("discordUserId").GetString()!);
        var okItem = byId[ok];
        okItem.GetProperty("rank").GetProperty("roleId").GetString().Should().Be(officier);
        okItem.GetProperty("rank").GetProperty("name").GetString().Should().Be("Officier");
        okItem.GetProperty("rank").GetProperty("color").GetString().Should().Be("#ff0000");
        okItem.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("roleId").GetString())
            .Should().Equal(pilote, officier, recrue);
        okItem.GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("handle").GetString(), l.GetProperty("citizenId").GetInt32(), l.GetProperty("displayName").GetString()))
            .Should().Equal(($"Ok{n}", Cid(n, 1), "Okay"));
        okItem.GetProperty("rsiRank").GetString().Should().Be("Officer");
        okItem.GetProperty("reconciliation").GetString().Should().Be("ok");
        okItem.GetProperty("multipleLinks").GetBoolean().Should().BeFalse();
        byId[mismatch].GetProperty("reconciliation").GetString().Should().Be("rank_mismatch");
        byId[mismatch].GetProperty("rsiRank").GetString().Should().Be("Recruit");
        byId[outsider].GetProperty("reconciliation").GetString().Should().Be("not_in_rsi_org");
        byId[outsider].GetProperty("multipleLinks").GetBoolean().Should().BeTrue();
        byId[outsider].GetProperty("links").GetArrayLength().Should().Be(2);
        byId[outsider].GetProperty("rank").GetProperty("roleId").GetString().Should().Be(recrue);
        byId[unlinked].GetProperty("reconciliation").GetString().Should().Be("unlinked");
        byId[unlinked].GetProperty("links").GetArrayLength().Should().Be(0);
        byId[bot].GetProperty("reconciliation").ValueKind.Should().Be(JsonValueKind.Null);
        byId[bot].GetProperty("rank").GetProperty("roleId").GetString().Should().Be(officier);

        Usernames(await GetOkAsync(client, $"{url}?reconciliation=not_in_rsi_org")).Should().Equal($"m-outsider{n}");
        Usernames(await GetOkAsync(client, $"{url}?reconciliation=unlinked")).Should().Equal($"m-unlinked{n}");
        // m-ok holds Recrue too, but their rank is Officier.
        var recruits = await GetOkAsync(client, $"{url}?rankRoleId={recrue}");
        Usernames(recruits).Should().Equal($"m-outsider{n}", $"m-unlinked{n}");
        recruits.GetProperty("total").GetInt32().Should().Be(2);
        var secondRecruit = await GetOkAsync(client, $"{url}?rankRoleId={recrue}&page=2&pageSize=1");
        Usernames(secondRecruit).Should().Equal($"m-unlinked{n}");
        secondRecruit.GetProperty("total").GetInt32().Should().Be(2);
        Usernames(await GetOkAsync(client, $"{url}?rankRoleId={officier}&reconciliation=ok")).Should().Equal($"m-ok{n}");
        Usernames(await GetOkAsync(client, $"{url}?rankRoleId={pilote}")).Should().BeEmpty("Pilote is not a rank");
        (await client.GetAsync($"{url}?reconciliation=everything")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"{url}?rankRoleId=officier")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_AreNewestFirst_WithTheirSender_AndTheRankChangeOfARoleChange()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var otherGuild = await _seed.SeedGuildAsync(null);
        var officier = await _seed.SeedRoleAsync(guildId, "Officier", 20, isRank: true);
        var recrue = await _seed.SeedRoleAsync(guildId, "Recrue", 10, isRank: true);
        var pilote = await _seed.SeedRoleAsync(guildId, "Pilote", 30, isRank: false);
        var member = await _seed.SeedMemberAsync(guildId, $"ev-member{n}");
        var other = await _seed.SeedMemberAsync(guildId, $"ev-other{n}");
        var outsider = await _seed.SeedMemberAsync(otherGuild, $"ev-outsider{n}");
        var first = await SeedSyncAsync(guildId, $"alice{n}", At.AddDays(-3));
        var second = await SeedSyncAsync(guildId, $"bob{n}", At.AddDays(-2));
        var joined = await SeedEventAsync(guildId, member, first, DiscordEventTypes.Joined, null, "2026-08-29T12:00:00Z",
            occurredAt: At.AddDays(-3));
        var promoted = await SeedEventAsync(guildId, member, second, DiscordEventTypes.RolesChanged,
            RoleList((recrue, "Recrue"), (pilote, "Pilote")), RoleList((officier, "Officier de bord"), (pilote, "Pilote")),
            notBefore: At.AddDays(-3));
        var flair = await SeedEventAsync(guildId, other, second, DiscordEventTypes.RolesChanged,
            RoleList((pilote, "Pilote")), "[]", notBefore: At.AddDays(-3));
        var renamed = await SeedEventAsync(null, member, second, DiscordEventTypes.UsernameChanged, $"old{n}", $"ev-member{n}");
        await SeedEventAsync(otherGuild, outsider, second, DiscordEventTypes.Joined, null, null);
        await SeedEventAsync(null, outsider, second, DiscordEventTypes.UsernameChanged, $"x{n}", $"ev-outsider{n}");
        var purged = await SeedEventAsync(guildId, other, 999_999_999, DiscordEventTypes.NickChanged, null, "Pseudo");
        var client = await factory.SignedInClientAsync($"c3-events-{n}");
        var url = $"/api/discord/guilds/{guildId}/events";

        var events = (await GetOkAsync(client, url)).EnumerateArray().ToList();

        events.Select(e => e.GetProperty("id").GetInt64()).Should().Equal(purged, renamed, flair, promoted, joined);
        var byId = events.ToDictionary(e => e.GetProperty("id").GetInt64());
        var rankChange = byId[promoted].GetProperty("rankChange");
        rankChange.GetProperty("from").GetString().Should().Be("Recrue");
        rankChange.GetProperty("to").GetString().Should().Be("Officier de bord", "the rank is named as it was then");
        byId[promoted].GetProperty("submittedBy").GetString().Should().Be($"bob{n}");
        byId[promoted].GetProperty("username").GetString().Should().Be($"ev-member{n}");
        byId[promoted].GetProperty("notBefore").GetDateTime().Should().Be(At.AddDays(-3));
        byId[flair].GetProperty("rankChange").ValueKind.Should().Be(JsonValueKind.Null, "the rank did not change");
        byId[joined].GetProperty("rankChange").ValueKind.Should().Be(JsonValueKind.Null);
        byId[joined].GetProperty("submittedBy").GetString().Should().Be($"alice{n}");
        byId[joined].GetProperty("occurredAt").GetDateTime().Should().Be(At.AddDays(-3));
        byId[joined].GetProperty("type").GetString().Should().Be("joined");
        byId[renamed].GetProperty("guildId").ValueKind.Should().Be(JsonValueKind.Null);
        byId[renamed].GetProperty("oldValue").GetString().Should().Be($"old{n}");
        byId[purged].GetProperty("submittedBy").ValueKind.Should().Be(JsonValueKind.Null, "its sync was purged");

        Ids(await GetOkAsync(client, $"{url}?type=roles_changed")).Should().Equal(flair, promoted);
        Ids(await GetOkAsync(client, $"{url}?userId={member}")).Should().Equal(renamed, promoted, joined);
        Ids(await GetOkAsync(client, $"{url}?limit=2")).Should().Equal(purged, renamed);
        (await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/events")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Syncs_AreNewestFirst_AndLimited()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        var first = await SeedSyncAsync(guildId, $"a{n}", At.AddDays(-3));
        var guarded = await SeedSyncAsync(guildId, $"b{n}", At.AddDays(-2), isComplete: false, guard: true, method: DiscordSyncMethods.Cache);
        var last = await SeedSyncAsync(guildId, $"c{n}", At.AddDays(-1));
        var client = await factory.SignedInClientAsync($"c3-syncs-{n}");
        var url = $"/api/discord/guilds/{guildId}/syncs";

        var syncs = (await GetOkAsync(client, url)).EnumerateArray().ToList();

        syncs.Select(s => s.GetProperty("id").GetInt64()).Should().Equal(last, guarded, first);
        var middle = syncs[1];
        middle.GetProperty("submittedBy").GetString().Should().Be($"b{n}");
        middle.GetProperty("method").GetString().Should().Be("cache");
        middle.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        middle.GetProperty("declaredComplete").GetBoolean().Should().BeFalse();
        middle.GetProperty("massDepartureDetected").GetBoolean().Should().BeTrue();
        middle.GetProperty("receivedAt").GetDateTime().Should().Be(At.AddDays(-2));
        middle.GetProperty("collectedAt").GetDateTime().Should().Be(At.AddDays(-2).AddMinutes(-5));
        middle.GetProperty("expectedCount").GetInt32().Should().Be(4);
        middle.GetProperty("collectedCount").GetInt32().Should().Be(4);
        middle.GetProperty("pluginVersion").GetString().Should().Be("1.0.0");
        Ids(await GetOkAsync(client, $"{url}?limit=2")).Should().Equal(last, guarded);
        (await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/syncs")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    private static List<string?> Usernames(JsonElement page)
        => Items(page).Select(m => m.GetProperty("username").GetString()).ToList();

    private static List<long> Ids(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToList();

    private static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();

    /// <summary>A roles_changed value as the ingest stores it: [{"id","name"}], ordered by id.</summary>
    private static string RoleList(params (string Id, string Name)[] roles)
        => JsonSerializer.Serialize(roles.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new { id = r.Id, name = r.Name }));

    private Task<long> SeedSyncAsync(
        string guildId, string by, DateTime receivedAt, bool isComplete = true, bool guard = false,
        string method = DiscordSyncMethods.MemberSearch)
        => _seed.ReadAsync(async db =>
        {
            var sync = new DiscordSync
            {
                GuildId = guildId, SubmittedByApiUserId = 1, SubmittedByUsername = by,
                ReceivedAt = receivedAt, CollectedAt = receivedAt.AddMinutes(-5), DeclaredCollectedAt = receivedAt.AddMinutes(-5),
                Method = method, DeclaredComplete = isComplete, IsComplete = isComplete, IsBaseline = false,
                MassDepartureDetected = guard, ExpectedCount = 4, CollectedCount = 4, OptedOutCount = 0,
                UnknownRoleRefCount = 0, EventCount = 0, PluginVersion = "1.0.0",
            };
            db.DiscordSyncs.Add(sync);
            await db.SaveChangesAsync();
            return sync.Id;
        });

    private Task<long> SeedEventAsync(
        string? guildId, string userId, long syncId, string type, string? oldValue, string? newValue,
        DateTime? occurredAt = null, DateTime? notBefore = null)
        => _seed.ReadAsync(async db =>
        {
            var row = new DiscordMemberEvent
            {
                GuildId = guildId, DiscordUserId = userId, SyncId = syncId, Type = type,
                OldValue = oldValue, NewValue = newValue, OccurredAt = occurredAt, NotBefore = notBefore, ObservedAt = At,
            };
            db.DiscordMemberEvents.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
}
