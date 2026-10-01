using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// GET api/users/{handle}/discord (spec § 10.4) and GET api/organizations/{sid}/discord:
/// linked accounts and their guilds, the combined RSI + Discord timeline within each
/// handle's window, empty lists versus 404, and org guilds that follow re-mapping.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordProfileTests(ApiFactory factory)
{
    private static int _seq = 74_000;
    private readonly DiscordReadSeed _seed = new(factory);

    private static DateTime At => DiscordReadSeed.At;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_740_000_000 + n * 20 + k;

    [Fact]
    public async Task AProfile_ListsLinkedAccountsAndGuilds_WithACombinedTimeline_WithinEachHandlesWindow()
    {
        var n = Next();
        var cid = Cid(n, 1);
        var sid = $"PRF{n}";
        var renamedAt = At.AddDays(-50);
        await SeedUserAsync(cid, $"Pilot{n}");
        await SeedHistoryAsync(cid, $"OldPilot{n}", firstSeen: At.AddDays(-100), lastSeen: renamedAt.AddDays(-1));
        await SeedHistoryAsync(cid, $"Pilot{n}", firstSeen: renamedAt, lastSeen: At);
        var discordId = DiscordTestKit.NewSnowflake();
        var entityId = await _seed.SeedPersonAsync(cid, $"Pilot{n}", "Pilot", discordId);
        // A legacy link that is not a Discord id: no account, ignored.
        await SeedLinkAsync(entityId, "pilot#1234");
        var mapped = await _seed.SeedGuildAsync(sid, name: $"Alpha {n}");
        var unmapped = await _seed.SeedGuildAsync(null, name: $"Bravo {n}");
        var officier = await _seed.SeedRoleAsync(mapped, "Officier", 20, isRank: true);
        await _seed.SeedMemberAsync(mapped, $"pilot{n}", [officier], globalName: "Pilot", userId: discordId, joinedAt: At.AddDays(-40));
        await _seed.SeedMemberAsync(unmapped, $"pilot{n}", userId: discordId, left: true, joinedAt: At.AddDays(-45));
        await SeedChangeAsync($"OldPilot{n}", "member_joined", sid, At.AddDays(-80));
        // After the rename the old handle may belong to someone else: out of this timeline.
        await SeedChangeAsync($"OldPilot{n}", "rank_changed", $"OTHER{n}", At.AddDays(-10));
        await SeedChangeAsync($"Pilot{n}", "handle_changed", sid, renamedAt);
        await SeedChangeAsync($"Pilot{n}", "member_left", sid, At.AddDays(-5));
        // Not a timeline type.
        await SeedChangeAsync($"Pilot{n}", "display_name_changed", null, At.AddDays(-4));
        await SeedDiscordEventAsync(mapped, discordId, DiscordEventTypes.Joined, observedAt: At.AddDays(-39), occurredAt: At.AddDays(-40));
        await SeedDiscordEventAsync(unmapped, discordId, DiscordEventTypes.Left, observedAt: At.AddDays(-2), notBefore: At.AddDays(-3));
        await SeedDiscordEventAsync(null, discordId, DiscordEventTypes.UsernameChanged, observedAt: At.AddDays(-1),
            notBefore: At.AddDays(-2), oldValue: $"old{n}", newValue: $"pilot{n}");
        var client = await factory.SignedInClientAsync($"c4-profile-{n}");

        var profile = await GetOkAsync(client, $"/api/users/PILOT{n}/discord");

        var account = profile.GetProperty("accounts").EnumerateArray().Should().ContainSingle().Subject;
        account.GetProperty("discordUserId").GetString().Should().Be(discordId);
        account.GetProperty("username").GetString().Should().Be($"pilot{n}");
        account.GetProperty("globalName").GetString().Should().Be("Pilot");
        account.GetProperty("guilds").EnumerateArray()
            .Select(g => (g.GetProperty("guildId").GetString(), g.GetProperty("guildName").GetString(),
                g.GetProperty("orgSid").GetString(), g.GetProperty("rank").GetString(),
                Date(g.GetProperty("joinedAt")), Date(g.GetProperty("leftAt")), g.GetProperty("lastSeenAt").GetDateTime()))
            .Should().Equal(
                (mapped, $"Alpha {n}", sid, "Officier", At.AddDays(-40), null, At),
                (unmapped, $"Bravo {n}", null, null, At.AddDays(-45), At, At));
        profile.GetProperty("timeline").EnumerateArray().Select(Entry).Should().Equal(
            ("discord", "username_changed", At.AddDays(-1), At.AddDays(-2), null, null, null),
            ("discord", "left", At.AddDays(-2), At.AddDays(-3), null, unmapped, $"Bravo {n}"),
            ("rsi", "member_left", At.AddDays(-5), null, sid, null, null),
            ("discord", "joined", At.AddDays(-40), null, sid, mapped, $"Alpha {n}"),
            ("rsi", "handle_changed", renamedAt, null, sid, null, null),
            ("rsi", "member_joined", At.AddDays(-80), null, sid, null, null));
    }

    [Fact]
    public async Task ACurrentReusedHandle_KeepsOnlyTheNewOwnersObservedIntervalAndIdentifiedRenames()
    {
        var n = Next();
        var owner = Cid(n, 1);
        var formerOwner = Cid(n, 2);
        var handle = $"Reused{n}";
        var sid = $"OWN{n}";
        await SeedUserAsync(formerOwner, handle, At.AddDays(-30)); // stale profile, still holding the handle
        await SeedHistoryAsync(formerOwner, handle, At.AddDays(-100), At.AddDays(-30));
        await SeedUserAsync(owner, handle);
        await SeedHistoryAsync(owner, handle, At.AddDays(-20), At.AddDays(-19));
        var discordId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(owner, handle, null, discordId);
        var guild = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guild, "newowner", userId: discordId);
        await SeedChangeAsync(handle, "rank_changed", $"OLD{n}", At.AddDays(-80));
        await SeedChangeAsync(handle, "rank_changed", $"GAP{n}", At.AddDays(-25));
        await SeedChangeAsync(handle, "handle_changed", $"GAP{n}", At.AddDays(-21));
        await SeedChangeAsync(handle, "member_joined", sid, At.AddDays(-20));
        await SeedChangeAsync(handle, "rank_changed", sid, At.AddDays(-10));
        await SeedChangeAsync(handle, "member_left", sid, At);
        // The handle alone would wrongly attribute this former owner's user event to the new one.
        await SeedIdentifiedRenameAsync(formerOwner, handle, $"Elsewhere{n}", At.AddDays(-10));
        await SeedIdentifiedRenameAsync(owner, $"Previous{n}", handle, At.AddDays(-25));
        var client = await factory.SignedInClientAsync($"c4-reused-current-{n}");

        var timeline = (await GetOkAsync(client, $"/api/users/{handle}/discord"))
            .GetProperty("timeline").EnumerateArray().ToList();

        timeline.Select(e => (e.GetProperty("type").GetString(), e.GetProperty("at").GetDateTime()))
            .Should().Equal(("member_left", At), ("rank_changed", At.AddDays(-10)),
                ("member_joined", At.AddDays(-20)), ("handle_changed", At.AddDays(-25)));
        timeline.Last().GetProperty("oldValue").GetString().Should().Be($"Previous{n}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandleTakenBackByTheSameCitizen_KeepsSeparatePassesWithoutBridgingGaps(bool anotherOwnerObserved)
    {
        var n = Next();
        var owner = Cid(n, 1);
        var handle = $"Returned{n}";
        var otherHandle = $"Away{n}";
        var sid = $"OWN{n}";
        await SeedUserAsync(owner, handle);
        await SeedHistoryAsync(owner, handle, At.AddDays(-100), At.AddDays(-70));
        await SeedHistoryAsync(owner, otherHandle, At.AddDays(-60), At.AddDays(-30));
        await SeedHistoryAsync(owner, handle, At.AddDays(-20), At.AddDays(-1));
        if (anotherOwnerObserved)
            await SeedHistoryAsync(Cid(n, 2), handle, At.AddDays(-55), At.AddDays(-35));
        var discordId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(owner, handle, null, discordId);
        var guild = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guild, "returned", userId: discordId);
        await SeedChangeAsync(handle, "member_joined", sid, At.AddDays(-100));
        await SeedChangeAsync(handle, "rank_changed", sid, At.AddDays(-80));
        await SeedChangeAsync(handle, "rank_changed", $"GAP{n}", At.AddDays(-65));
        await SeedChangeAsync(handle, "rank_changed", $"OTHER{n}", At.AddDays(-50));
        await SeedChangeAsync(otherHandle, "rank_changed", sid, At.AddDays(-40));
        await SeedChangeAsync(handle, "rank_changed", $"GAP{n}", At.AddDays(-25));
        await SeedChangeAsync(handle, "member_joined", sid, At.AddDays(-20));
        await SeedChangeAsync(handle, "rank_changed", sid, At.AddDays(-10));
        await SeedIdentifiedRenameAsync(owner, handle, otherHandle, At.AddDays(-60));
        await SeedIdentifiedRenameAsync(owner, otherHandle, handle, At.AddDays(-20));
        var client = await factory.SignedInClientAsync($"c4-returned-{n}");

        var timeline = (await GetOkAsync(client, $"/api/users/{handle}/discord"))
            .GetProperty("timeline").EnumerateArray().ToList();

        timeline.Select(e => e.GetProperty("at").GetDateTime()).Should().Equal(
            At.AddDays(-10), At.AddDays(-20), At.AddDays(-20), At.AddDays(-40),
            At.AddDays(-60), At.AddDays(-80), At.AddDays(-100));
        timeline.Where(e => e.GetProperty("type").GetString() == "handle_changed").Should().HaveCount(2);
        timeline.Where(e => e.GetProperty("orgSid").ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty("orgSid").GetString()).Should().OnlyContain(s => s == sid);
    }

    [Theory]
    [InlineData(-40, -10, 0, -50)]
    [InlineData(-60, -10, 0, null)]
    [InlineData(-40, 10, -50, null)]
    [InlineData(-60, 10, null, null)]
    public async Task OverlappingOwnerObservations_KeepOnlyUncontestedObservationPoints(
        int otherFirstDay, int otherLastDay, int? expectedFirstDay, int? expectedSecondDay)
    {
        var n = Next();
        var owner = Cid(n, 1);
        var handle = $"Overlap{n}";
        await SeedUserAsync(owner, handle);
        await SeedHistoryAsync(owner, handle, At.AddDays(-50), At.AddDays(-1));
        // Legacy aggregated observations overlap: they cannot prove a continuous ownership period.
        await SeedHistoryAsync(Cid(n, 2), handle, At.AddDays(otherFirstDay), At.AddDays(otherLastDay));
        var discordId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(owner, handle, null, discordId);
        var guild = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guild, "overlap", userId: discordId);
        foreach (var days in new[] { -50, -45, -40, -25, -10, -5, 0 })
            await SeedChangeAsync(handle, "rank_changed", $"OV{n}", At.AddDays(days));
        var client = await factory.SignedInClientAsync($"c4-overlap-{n}");

        var timeline = (await GetOkAsync(client, $"/api/users/{handle}/discord"))
            .GetProperty("timeline").EnumerateArray().ToList();

        timeline.Select(e => e.GetProperty("at").GetDateTime()).Should().Equal(
            new[] { expectedFirstDay, expectedSecondDay }.Where(day => day.HasValue)
                .Select(day => At.AddDays(day!.Value)));
    }

    [Fact]
    public async Task AnOrdinaryHandle_DoesNotTreatFirstSeenAsAnExactAcquisitionDate()
    {
        var n = Next();
        var owner = Cid(n, 1);
        var handle = $"Ordinary{n}";
        await SeedUserAsync(owner, handle);
        await SeedHistoryAsync(owner, handle, At.AddDays(-20), At.AddDays(-1));
        var discordId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(owner, handle, null, discordId);
        var guild = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guild, "ordinary", userId: discordId);
        await SeedChangeAsync(handle, "member_joined", $"OR{n}", At.AddDays(-80));
        var client = await factory.SignedInClientAsync($"c4-ordinary-{n}");

        var timeline = (await GetOkAsync(client, $"/api/users/{handle}/discord"))
            .GetProperty("timeline").EnumerateArray().ToList();

        timeline.Select(e => e.GetProperty("at").GetDateTime()).Should().Equal(At.AddDays(-80));
    }

    [Fact]
    public async Task AFormerHandle_ACaseVariant_OrAnEntityWithoutCitizenId_ResolveThePerson()
    {
        var n = Next();
        var cid = Cid(n, 1);
        await SeedUserAsync(cid, $"Current{n}");
        await SeedHistoryAsync(cid, $"Former{n}", firstSeen: At.AddDays(-60), lastSeen: At.AddDays(-31));
        await SeedHistoryAsync(cid, $"Current{n}", firstSeen: At.AddDays(-30), lastSeen: At);
        var discordId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(cid, $"Current{n}", null, discordId);
        var guildId = await _seed.SeedGuildAsync(null);
        await _seed.SeedMemberAsync(guildId, $"current{n}", userId: discordId);
        // A person with no citizen id, linked by hand: their RSI part reads their current handle.
        var rosterOnlyId = DiscordTestKit.NewSnowflake();
        await _seed.SeedPersonAsync(null, $"RosterOnly{n}", null, rosterOnlyId);
        await _seed.SeedMemberAsync(guildId, $"rosteronly{n}", userId: rosterOnlyId);
        await SeedChangeAsync($"RosterOnly{n}", "member_joined", $"RO{n}", At.AddDays(-7));
        var client = await factory.SignedInClientAsync($"c4-resolve-{n}");

        foreach (var handle in new[] { $"former{n}", $"CURRENT{n}", $"Current{n}" })
        {
            var profile = await GetOkAsync(client, $"/api/users/{handle}/discord");
            AccountIds(profile).Should().Equal(new[] { discordId }, handle);
        }
        var rosterOnly = await GetOkAsync(client, $"/api/users/rosteronly{n}/discord");
        AccountIds(rosterOnly).Should().Equal(rosterOnlyId);
        rosterOnly.GetProperty("timeline").EnumerateArray().Select(Entry).Should().Equal(
            ("rsi", "member_joined", At.AddDays(-7), null, $"RO{n}", null, null));
    }

    [Fact]
    public async Task APersonWithoutDiscordData_GetsEmptyLists_AndAnUnknownHandleIs404()
    {
        var n = Next();
        // Known to users only.
        await SeedUserAsync(Cid(n, 1), $"UserOnly{n}");
        // Tracked, with RSI history, but no discord link.
        await SeedUserAsync(Cid(n, 2), $"NoDiscord{n}");
        await _seed.SeedPersonAsync(Cid(n, 2), $"NoDiscord{n}", null);
        await SeedChangeAsync($"NoDiscord{n}", "member_joined", $"ND{n}", At.AddDays(-3));
        // Linked to a Discord id no sync ever carried.
        await _seed.SeedPersonAsync(Cid(n, 3), $"NeverSynced{n}", null, DiscordTestKit.NewSnowflake());
        // Known to an org roster only.
        await _seed.SeedRosterAsync($"RO{n}", $"RosterOnly{n}", null, "Member");
        var client = await factory.SignedInClientAsync($"c4-empty-{n}");

        foreach (var handle in new[] { $"UserOnly{n}", $"nodiscord{n}", $"NeverSynced{n}", $"rosteronly{n}" })
        {
            var profile = await GetOkAsync(client, $"/api/users/{handle}/discord");
            profile.GetProperty("accounts").GetArrayLength().Should().Be(0, handle);
            profile.GetProperty("timeline").GetArrayLength().Should().Be(0, handle);
        }
        var unknown = await client.GetAsync($"/api/users/Nobody{n}/discord");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("detail").GetString().Should().Contain($"Nobody{n}");
    }

    [Fact]
    public async Task AnOrg_ListsTheGuildsMappedToIt_WithTheirHeadcounts()
    {
        var n = Next();
        var sid = $"POG{n}";
        var zeta = await _seed.SeedGuildAsync(sid, name: $"Zeta {n}");
        var alpha = await _seed.SeedGuildAsync(sid, name: $"alpha {n}", complete: false);
        var middle = await _seed.SeedGuildAsync(sid, name: $"Mid {n}");
        // Its last complete sync is older than its last sync: that one was partial.
        await _seed.WithDbAsync(db => db.DiscordGuilds.Where(g => g.GuildId == middle)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastCompleteSyncAt, (DateTime?)At.AddDays(-7))));
        await _seed.SeedGuildAsync(null, name: $"Unmapped {n}");
        var first = await _seed.SeedMemberAsync(zeta, $"og-a{n}");
        var second = await _seed.SeedMemberAsync(zeta, $"og-b{n}");
        await _seed.SeedMemberAsync(zeta, $"og-c{n}");
        var bot = await _seed.SeedMemberAsync(zeta, $"og-bot{n}", bot: true);
        var gone = await _seed.SeedMemberAsync(zeta, $"og-gone{n}", left: true);
        await _seed.SeedPersonAsync(Cid(n, 1), $"OgA{n}", null, first);
        await _seed.SeedPersonAsync(Cid(n, 2), $"OgB{n}", null, second);
        await _seed.SeedPersonAsync(Cid(n, 3), $"OgBot{n}", null, bot);
        await _seed.SeedPersonAsync(Cid(n, 4), $"OgGone{n}", null, gone);
        var client = await factory.SignedInClientAsync($"c4-org-{n}");

        var guilds = (await GetOkAsync(client, $"/api/organizations/{sid.ToLowerInvariant()}/discord")).EnumerateArray().ToList();

        guilds.Select(g => (g.GetProperty("guildId").GetString(), g.GetProperty("name").GetString(),
                g.GetProperty("activeMembers").GetInt32(), g.GetProperty("linkedMembers").GetInt32(),
                g.GetProperty("lastSyncComplete").GetBoolean()))
            .Should().Equal(
                (alpha, $"alpha {n}", 0, 0, false),
                (middle, $"Mid {n}", 0, 0, false),
                (zeta, $"Zeta {n}", 3, 2, true));
        guilds[2].GetProperty("lastSyncAt").GetDateTime().Should().Be(At);
        guilds[2].GetProperty("iconHash").ValueKind.Should().Be(JsonValueKind.Null);
        (await GetOkAsync(client, $"/api/organizations/NONE{n}/discord")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ARemappedGuild_MovesFromTheOldOrgsListToTheNewOne()
    {
        var n = Next();
        var sidA = $"PRA{n}";
        var sidB = $"PRB{n}";
        var guildId = await _seed.SeedGuildAsync(sidA);
        var client = await factory.SignedInClientAsync($"c4-remap-{n}");

        (await OrgGuildIdsAsync(client, sidA)).Should().Equal(guildId);
        (await OrgGuildIdsAsync(client, sidB)).Should().BeEmpty();

        await _seed.SetGuildOrgAsync(guildId, sidB);

        (await OrgGuildIdsAsync(client, sidA)).Should().BeEmpty();
        (await OrgGuildIdsAsync(client, sidB)).Should().Equal(guildId);
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<string?>> OrgGuildIdsAsync(HttpClient client, string sid)
        => (await GetOkAsync(client, $"/api/organizations/{sid}/discord")).EnumerateArray()
            .Select(g => g.GetProperty("guildId").GetString()).ToList();

    private static List<string?> AccountIds(JsonElement profile)
        => profile.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("discordUserId").GetString()).ToList();

    private static (string?, string?, DateTime, DateTime?, string?, string?, string?) Entry(JsonElement e) => (
        e.GetProperty("source").GetString(),
        e.GetProperty("type").GetString(),
        e.GetProperty("at").GetDateTime(),
        Date(e.GetProperty("notBefore")),
        e.GetProperty("orgSid").GetString(),
        e.GetProperty("guildId").GetString(),
        e.GetProperty("guildName").GetString());

    private static DateTime? Date(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDateTime();

    private Task SeedUserAsync(int citizenId, string handle, DateTime? observedAt = null) => _seed.WithDbAsync(async db =>
    {
        db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = observedAt ?? At, UpdatedAt = observedAt ?? At });
        await db.SaveChangesAsync();
    });

    private Task SeedHistoryAsync(int citizenId, string handle, DateTime firstSeen, DateTime lastSeen) => _seed.WithDbAsync(async db =>
    {
        db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = citizenId, UserHandle = handle, FirstSeen = firstSeen, LastSeen = lastSeen,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedLinkAsync(long entityId, string value) => _seed.WithDbAsync(async db =>
    {
        db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entityId, Provider = LinkProviders.Discord, Value = value,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = At, UpdatedAt = At,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedChangeAsync(string handle, string type, string? orgSid, DateTime at) => _seed.WithDbAsync(async db =>
    {
        db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = at, EntityType = "member", EntityId = handle, ChangeType = type,
            OrgSid = orgSid, UserHandle = handle,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedIdentifiedRenameAsync(int citizenId, string oldHandle, string newHandle, DateTime at) => _seed.WithDbAsync(async db =>
    {
        db.ChangeEvents.Add(new ChangeEvent
        {
            Timestamp = at, EntityType = "user", EntityId = citizenId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ChangeType = "handle_changed", UserHandle = oldHandle, OldValue = oldHandle, NewValue = newHandle,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedDiscordEventAsync(
        string? guildId, string userId, string type, DateTime observedAt, DateTime? occurredAt = null,
        DateTime? notBefore = null, string? oldValue = null, string? newValue = null) => _seed.WithDbAsync(async db =>
    {
        db.DiscordMemberEvents.Add(new DiscordMemberEvent
        {
            GuildId = guildId, DiscordUserId = userId, SyncId = 1, Type = type, OldValue = oldValue, NewValue = newValue,
            OccurredAt = occurredAt, NotBefore = notBefore, ObservedAt = observedAt,
        });
        await db.SaveChangesAsync();
    });
}
