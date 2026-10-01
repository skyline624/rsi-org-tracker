using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Services.Discord;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Reconciliation of Discord members with the RSI roster of the guild's org (spec § 10.2),
/// discrepancies and totals, and multi-membership (spec § 10.3).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordReconciliationTests(ApiFactory factory)
{
    private static int _seq = 72_000;
    private readonly DiscordReadSeed _seed = new(factory);

    private static DateTime At => DiscordReadSeed.At;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_720_000_000 + n * 20 + k;

    [Fact]
    public async Task EachStatus_IsComputedAgainstTheActiveRosterOfTheMappedOrg()
    {
        var n = Next();
        var sid = $"RCS{n}";
        await _seed.SeedRosterAsync(sid, $"OkPilot{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sid, $"Mismatch{n}", Cid(n, 2), "Recruit");
        await _seed.SeedRosterAsync(sid, $"CasePilot{n}", null, "Officer");
        await _seed.SeedRosterAsync(sid, $"NewName{n}", Cid(n, 4), "Officer");
        await _seed.SeedRosterAsync(sid, $"Reused{n}", Cid(n, 6), "Officer");
        await _seed.SeedRosterAsync(sid, $"AnyIn{n}", Cid(n, 8), "Officer");
        await _seed.SeedRosterAsync(sid, $"NoLabel{n}", Cid(n, 9), "Whatever");
        await _seed.SeedRosterAsync(sid, $"Gone{n}", Cid(n, 10), "Officer", active: false);
        var ok = DiscordTestKit.NewSnowflake();
        var mismatch = DiscordTestKit.NewSnowflake();
        var notIn = DiscordTestKit.NewSnowflake();
        var byHandle = DiscordTestKit.NewSnowflake();
        var byCitizenId = DiscordTestKit.NewSnowflake();
        var reused = DiscordTestKit.NewSnowflake();
        var any = DiscordTestKit.NewSnowflake();
        var noLabel = DiscordTestKit.NewSnowflake();
        var gone = DiscordTestKit.NewSnowflake();
        var unlinked = DiscordTestKit.NewSnowflake();
        var bot = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(Cid(n, 1), $"OkPilot{n}", null, ok);
        await _seed.SeedPersonAsync(Cid(n, 2), $"Mismatch{n}", null, mismatch);
        await _seed.SeedPersonAsync(Cid(n, 3), $"Outsider{n}", null, notIn);
        // No citizen id on either side: matched by handle, regardless of case.
        await _seed.SeedPersonAsync(null, $"casepilot{n}", null, byHandle);
        // A stale handle on the entity: the citizen id is matched first.
        await _seed.SeedPersonAsync(Cid(n, 4), $"OldName{n}", null, byCitizenId);
        // The roster row with this handle belongs to another citizen.
        await _seed.SeedPersonAsync(Cid(n, 5), $"Reused{n}", null, reused);
        // Two people behind one account: one of them in the roster with a coherent rank is enough.
        await _seed.SeedPersonAsync(Cid(n, 7), $"AnyOut{n}", null, any);
        await _seed.SeedPersonAsync(Cid(n, 8), $"AnyIn{n}", null, any);
        await _seed.SeedPersonAsync(Cid(n, 9), $"NoLabel{n}", null, noLabel);
        await _seed.SeedPersonAsync(Cid(n, 10), $"Gone{n}", null, gone);
        await _seed.SeedPersonAsync(Cid(n, 11), $"BotOwner{n}", null, bot);
        ReconciliationSubject[] subjects =
        [
            new(ok, false, " officer "), new(mismatch, false, "Officer"), new(notIn, false, "Officer"),
            new(byHandle, false, "OFFICER"), new(byCitizenId, false, "Officer"), new(reused, false, "Officer"),
            new(any, false, "Officer"), new(noLabel, false, null), new(gone, false, "Officer"),
            new(unlinked, false, "Officer"), new(bot, true, "Officer"),
        ];

        var statuses = await ReconcileAsync(sid, subjects);

        statuses.ToDictionary(p => p.Key, p => p.Value.Status).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            [ok] = "ok",
            [mismatch] = "rank_mismatch",
            [notIn] = "not_in_rsi_org",
            [byHandle] = "ok",
            [byCitizenId] = "ok",
            [reused] = "not_in_rsi_org",
            [any] = "ok",
            [noLabel] = "ok",
            [gone] = "not_in_rsi_org",
            [unlinked] = "unlinked",
            [bot] = null,
        });
        statuses[ok].RsiRank.Should().Be("Officer");
        statuses[mismatch].RsiRank.Should().Be("Recruit");
        statuses[mismatch].Person!.Handle.Should().Be($"Mismatch{n}");
        statuses[noLabel].RsiRank.Should().Be("Whatever");
        statuses[notIn].RsiRank.Should().BeNull();
        statuses[any].MultipleLinks.Should().BeTrue();
        statuses[any].Links.Select(l => l.Handle).Should().Equal($"AnyOut{n}", $"AnyIn{n}");
        statuses[any].Person!.Handle.Should().Be($"AnyIn{n}");
        statuses.Where(p => p.Key != any).Should().OnlyContain(p => !p.Value.MultipleLinks);
        statuses[unlinked].Links.Should().BeEmpty();

        // An unmapped guild: no status at all, links still listed.
        var unmapped = await ReconcileAsync(null, subjects);
        unmapped.Values.Should().OnlyContain(r => r.Status == null);
        unmapped[any].Links.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnOrgWithoutAnActiveRoster_LeavesEveryHumanRsiUnknown()
    {
        var n = Next();
        var sid = $"RCU{n}";
        await _seed.SeedRosterAsync(sid, $"Former{n}", Cid(n, 1), "Officer", active: false);
        var linked = DiscordTestKit.NewSnowflake();
        var unlinked = DiscordTestKit.NewSnowflake();
        var bot = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(Cid(n, 1), $"Former{n}", null, linked);

        var statuses = await ReconcileAsync(sid, [new(linked, false, null), new(unlinked, false, null), new(bot, true, null)]);

        statuses[linked].Status.Should().Be("rsi_unknown");
        statuses[unlinked].Status.Should().Be("rsi_unknown");
        statuses[bot].Status.Should().BeNull();
        statuses[linked].Links.Should().ContainSingle().Which.Handle.Should().Be($"Former{n}");
    }

    [Fact]
    public async Task Discrepancies_ListAbsencesRankMismatchesRsiOnly_AndTotalsFromTheLatestCounters()
    {
        var n = Next();
        var sid = $"RCD{n}";
        await _seed.SeedOrgAsync(sid, $"Org {sid}");
        await _seed.SeedRosterAsync(sid, $"Aligned{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sid, $"Promoted{n}", Cid(n, 2), "Recruit");
        await _seed.SeedRosterAsync(sid, $"Departed{n}", Cid(n, 4), "Officer");
        await _seed.SeedRosterAsync(sid, $"Lonely{n}", null, "Recruit");
        await _seed.SeedCountsAsync(sid, At.AddDays(-2), totalRows: 10, visible: 7, redacted: 2, hidden: 1);
        // The latest reading could not see every row: never fall back to the older breakdown.
        await _seed.SeedCountsAsync(sid, At.AddDays(-1), totalRows: 12, visible: null, redacted: null, hidden: null);
        var guildId = await _seed.SeedGuildAsync(sid);
        var officier = await _seed.SeedRoleAsync(guildId, "Officier", 20, isRank: true, rsiRankLabel: "Officer");
        var recrue = await _seed.SeedRoleAsync(guildId, "Recrue", 10, isRank: true, rsiRankLabel: "Recruit");
        var pilote = await _seed.SeedRoleAsync(guildId, "Pilote", 30, isRank: false);
        var aligned = await _seed.SeedMemberAsync(guildId, $"aligned{n}", [officier, pilote]);
        var promoted = await _seed.SeedMemberAsync(guildId, $"promoted{n}", [officier], nick: $"[{sid}] Promoted");
        var outsider = await _seed.SeedMemberAsync(guildId, $"outsider{n}", [recrue], globalName: "Outsider");
        await _seed.SeedMemberAsync(guildId, $"newcomer{n}");
        var bot = await _seed.SeedMemberAsync(guildId, $"bot{n}", [officier], bot: true);
        var departed = await _seed.SeedMemberAsync(guildId, $"departed{n}", [officier], left: true);
        await _seed.SeedPersonAsync(Cid(n, 1), $"Aligned{n}", null, aligned);
        await _seed.SeedPersonAsync(Cid(n, 2), $"Promoted{n}", null, promoted);
        await _seed.SeedPersonAsync(Cid(n, 3), $"Outsider{n}", null, outsider);
        await _seed.SeedPersonAsync(Cid(n, 4), $"Departed{n}", null, departed);
        await _seed.SeedPersonAsync(Cid(n, 5), $"BotOwner{n}", null, bot);
        var client = await factory.SignedInClientAsync($"c2-gaps-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

        body.GetProperty("orgSid").GetString().Should().Be(sid);
        body.GetProperty("rsiOnlyAvailable").GetBoolean().Should().BeTrue();
        Items(body).Should().Equal(
            ("not_in_rsi_org", $"Outsider{n}", Cid(n, 3), outsider, "Outsider", "Recrue", null),
            ("rank_mismatch", $"Promoted{n}", Cid(n, 2), promoted, $"[{sid}] Promoted", "Officier", "Recruit"),
            // Linked, but to an account that left the server.
            ("rsi_only", $"Departed{n}", Cid(n, 4), null, null, null, "Officer"),
            ("rsi_only", $"Lonely{n}", null, null, null, null, "Recruit"));
        var totals = body.GetProperty("totals");
        totals.GetProperty("discordActive").GetInt32().Should().Be(4, "bots and departed members are not counted");
        totals.GetProperty("discordLinked").GetInt32().Should().Be(3);
        totals.GetProperty("rsiTotalRows").GetInt32().Should().Be(12);
        totals.GetProperty("rsiVisible").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiRedacted").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiHidden").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiBreakdownKnown").GetBoolean().Should().BeFalse();
        totals.GetProperty("rsiCountsAt").GetDateTime().Should().Be(At.AddDays(-1));
    }

    [Fact]
    public async Task WithoutACompleteSync_RsiOnlyIsNotComputed()
    {
        var n = Next();
        var sid = $"RCP{n}";
        await _seed.SeedRosterAsync(sid, $"Lonely{n}", Cid(n, 1), "Officer");
        await _seed.SeedCountsAsync(sid, At, totalRows: 5, visible: 3, redacted: 1, hidden: 1);
        var guildId = await _seed.SeedGuildAsync(sid, complete: false);
        var outsider = await _seed.SeedMemberAsync(guildId, $"outsider{n}");
        await _seed.SeedPersonAsync(Cid(n, 2), $"Outsider{n}", null, outsider);
        var client = await factory.SignedInClientAsync($"c2-partial-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

        body.GetProperty("rsiOnlyAvailable").GetBoolean().Should().BeFalse();
        Items(body).Should().Equal(("not_in_rsi_org", $"Outsider{n}", Cid(n, 2), outsider, $"outsider{n}", null, null));
        var totals = body.GetProperty("totals");
        totals.GetProperty("rsiBreakdownKnown").GetBoolean().Should().BeTrue();
        totals.GetProperty("rsiVisible").GetInt32().Should().Be(3);
        totals.GetProperty("rsiRedacted").GetInt32().Should().Be(1);
        totals.GetProperty("rsiHidden").GetInt32().Should().Be(1);
        totals.GetProperty("rsiTotalRows").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task AnOrgWithoutRoster_GivesNoDiscrepancy_ButDiscordTotals()
    {
        var n = Next();
        var sid = $"RCN{n}";
        await _seed.SeedOrgAsync(sid, $"Org {sid}");
        var guildId = await _seed.SeedGuildAsync(sid);
        var member = await _seed.SeedMemberAsync(guildId, $"member{n}");
        await _seed.SeedPersonAsync(Cid(n, 1), $"Member{n}", null, member);
        var client = await factory.SignedInClientAsync($"c2-noroster-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

        Items(body).Should().BeEmpty();
        var totals = body.GetProperty("totals");
        totals.GetProperty("discordActive").GetInt32().Should().Be(1);
        totals.GetProperty("discordLinked").GetInt32().Should().Be(1);
        totals.GetProperty("rsiTotalRows").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiCountsAt").ValueKind.Should().Be(JsonValueKind.Null);
        totals.GetProperty("rsiBreakdownKnown").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AnUnmappedGuild_HasNoDiscrepancies_AndAnUnknownGuildIs404()
    {
        var n = Next();
        var guildId = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guildId, $"member{n}");
        var client = await factory.SignedInClientAsync($"c2-unmapped-{n}");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");
        var unknown = await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/discrepancies");

        body.GetProperty("orgSid").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("rsiOnlyAvailable").GetBoolean().Should().BeFalse();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("totals").ValueKind.Should().Be(JsonValueKind.Null);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARemappedGuild_IsReconciledAgainstItsNewOrg()
    {
        var n = Next();
        var sidA = $"RCA{n}";
        var sidB = $"RCB{n}";
        await _seed.SeedRosterAsync(sidA, $"Alpha{n}", Cid(n, 1), "Officer");
        await _seed.SeedRosterAsync(sidB, $"Bravo{n}", Cid(n, 2), "Officer");
        await _seed.SeedCountsAsync(sidA, At, totalRows: 5, visible: 5, redacted: 0, hidden: 0);
        await _seed.SeedCountsAsync(sidB, At, totalRows: 9, visible: 9, redacted: 0, hidden: 0);
        var guildId = await _seed.SeedGuildAsync(sidA);
        var member = await _seed.SeedMemberAsync(guildId, $"remap{n}");
        await _seed.SeedPersonAsync(Cid(n, 1), $"Alpha{n}", null, member);
        var client = await factory.SignedInClientAsync($"c2-remap-{n}");
        var url = $"/api/discord/guilds/{guildId}/discrepancies";

        var before = await client.GetFromJsonAsync<JsonElement>(url);
        await _seed.SetGuildOrgAsync(guildId, sidB);
        var after = await client.GetFromJsonAsync<JsonElement>(url);

        before.GetProperty("orgSid").GetString().Should().Be(sidA);
        Items(before).Should().BeEmpty();
        before.GetProperty("totals").GetProperty("rsiTotalRows").GetInt32().Should().Be(5);
        after.GetProperty("orgSid").GetString().Should().Be(sidB);
        Items(after).Should().Equal(
            ("not_in_rsi_org", $"Alpha{n}", Cid(n, 1), member, $"remap{n}", null, null),
            ("rsi_only", $"Bravo{n}", Cid(n, 2), null, null, null, "Officer"));
        after.GetProperty("totals").GetProperty("rsiTotalRows").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task MultiMembership_ListsHumansActiveInTwoGuilds_WithTheRsiOrgsOfTheirLinkedPeople()
    {
        var n = Next();
        var sidX = $"RMX{n}";
        var sidY = $"RMY{n}";
        var sidZ = $"RMZ{n}";
        await _seed.SeedRosterAsync(sidX, $"Main{n}", Cid(n, 1), "Boss");
        // A person without citizen id, found by handle regardless of case.
        await _seed.SeedRosterAsync(sidY, $"ALT{n}", null, "Grunt");
        await _seed.SeedRosterAsync(sidZ, $"Main{n}", Cid(n, 1), "Old", active: false);
        var alpha = await _seed.SeedGuildAsync(sidX, name: $"Alpha guild {n}");
        var bravo = await _seed.SeedGuildAsync(null, name: $"Bravo guild {n}");
        var officier = await _seed.SeedRoleAsync(alpha, "Officier", 20, isRank: true);
        var multi = await _seed.SeedMemberAsync(alpha, $"c2multi{n}", [officier], globalName: "Multi");
        await _seed.SeedMemberAsync(bravo, $"c2multi{n}", userId: multi);
        var loner = await _seed.SeedMemberAsync(alpha, $"c2loner{n}");
        await _seed.SeedMemberAsync(bravo, $"c2loner{n}", userId: loner);
        var leaver = await _seed.SeedMemberAsync(alpha, $"c2leaver{n}");
        await _seed.SeedMemberAsync(bravo, $"c2leaver{n}", userId: leaver, left: true);
        var bot = await _seed.SeedMemberAsync(alpha, $"c2bot{n}", bot: true);
        await _seed.SeedMemberAsync(bravo, $"c2bot{n}", userId: bot);
        await _seed.SeedPersonAsync(Cid(n, 1), $"Main{n}", "Main", multi);
        await _seed.SeedPersonAsync(null, $"Alt{n}", null, multi);
        var client = await factory.SignedInClientAsync($"c2-multi-{n}");

        var all = await AllMultiAsync(client);

        var ids = all.Select(i => i.GetProperty("discordUserId").GetString()).ToList();
        ids.Should().Contain(new[] { multi, loner }).And.NotContain(new[] { leaver, bot });
        var item = all.Single(i => i.GetProperty("discordUserId").GetString() == multi);
        item.GetProperty("username").GetString().Should().Be($"c2multi{n}");
        item.GetProperty("globalName").GetString().Should().Be("Multi");
        item.GetProperty("guilds").EnumerateArray()
            .Select(g => (g.GetProperty("guildId").GetString(), g.GetProperty("guildName").GetString(),
                g.GetProperty("orgSid").GetString(), g.GetProperty("rank").GetString()))
            .Should().Equal((alpha, $"Alpha guild {n}", sidX, "Officier"), (bravo, $"Bravo guild {n}", null, null));
        item.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("handle").GetString())
            .Should().Equal($"Main{n}", $"Alt{n}");
        item.GetProperty("rsiOrgs").EnumerateArray()
            .Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("rank").GetString()))
            .Should().Equal((sidX, "Boss"), (sidY, "Grunt"));
        var lonerItem = all.Single(i => i.GetProperty("discordUserId").GetString() == loner);
        lonerItem.GetProperty("links").GetArrayLength().Should().Be(0);
        lonerItem.GetProperty("rsiOrgs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MultiMembership_IsPagedByAccount()
    {
        var n = Next();
        var first = await _seed.SeedGuildAsync(null);
        var second = await _seed.SeedGuildAsync(null);
        for (var k = 0; k < 3; k++)
        {
            var id = await _seed.SeedMemberAsync(first, $"c2page{n}-{k}");
            await _seed.SeedMemberAsync(second, $"c2page{n}-{k}", userId: id);
        }
        var client = await factory.SignedInClientAsync($"c2-multipage-{n}");

        var one = await client.GetFromJsonAsync<JsonElement>("/api/discord/multi?page=1&pageSize=1");
        var two = await client.GetFromJsonAsync<JsonElement>("/api/discord/multi?page=2&pageSize=1");

        one.GetProperty("items").GetArrayLength().Should().Be(1);
        two.GetProperty("items").GetArrayLength().Should().Be(1);
        one.GetProperty("pageSize").GetInt32().Should().Be(1);
        one.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(3);
        one.GetProperty("totalPages").GetInt32().Should().Be(one.GetProperty("total").GetInt32());
        one.GetProperty("items")[0].GetProperty("discordUserId").GetString()
            .Should().NotBe(two.GetProperty("items")[0].GetProperty("discordUserId").GetString());
    }

    private async Task<IReadOnlyDictionary<string, MemberReconciliation>> ReconcileAsync(
        string? orgSid, IReadOnlyCollection<ReconciliationSubject> subjects)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DiscordReconciliationService>()
            .ReconcileAsync(orgSid, subjects, CancellationToken.None);
    }

    /// <summary>Every multi-membership row, walking the pages (other tests add rows to the shared database).</summary>
    private static async Task<List<JsonElement>> AllMultiAsync(HttpClient client)
    {
        var all = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var body = await client.GetFromJsonAsync<JsonElement>($"/api/discord/multi?page={page}&pageSize=200");
            all.AddRange(body.GetProperty("items").EnumerateArray());
            if (page >= body.GetProperty("totalPages").GetInt32()) return all;
        }
    }

    private static List<(string? Kind, string? Handle, int? CitizenId, string? DiscordUserId, string? DiscordName,
        string? DiscordRank, string? RsiRank)> Items(JsonElement body)
        => body.GetProperty("items").EnumerateArray()
            .Select(i => (
                i.GetProperty("kind").GetString(),
                i.GetProperty("handle").GetString(),
                Int(i.GetProperty("citizenId")),
                i.GetProperty("discordUserId").GetString(),
                i.GetProperty("discordName").GetString(),
                i.GetProperty("discordRank").GetString(),
                i.GetProperty("rsiRank").GetString()))
            .ToList();

    private static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
}
