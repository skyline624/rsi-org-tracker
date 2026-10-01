using System.Net;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Collector.Api.Controllers;
using Collector.Api.Data;
using Collector.Api.Dtos.Discord;
using Collector.Api.Options;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Discord.DiscordTestKit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// <c>POST api/ingest/discord/guilds/{guildId}/syncs</c> end to end (spec § 9 and § 15
/// "Ingestion"). It covers who may post, what each kind of sync records, and how the route
/// behaves under contention and with oversized or malformed bodies.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestTests(ApiFactory factory)
{
    /// <summary>
    /// Baselines are sent with the longest allowed collection, so they are dated 30 min in the
    /// past. A member who joined 10 min ago in a later sync therefore joined after tracking began.
    /// </summary>
    private const long BaselineDurationMs = 1_800_000;

    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-400);

    // --- access ---

    [Fact]
    public async Task ScopedKey_IsAccepted()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-scoped");
        var guild = NewSnowflake();

        var result = await PostOkAsync(client, guild, Sync(guild, [Member(NewSnowflake(), "scoped", joinedAt: LongAgo)], durationMs: 0));

        result.GetProperty("syncId").GetInt64().Should().BePositive();
        result.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        result.GetProperty("orgSid").ValueKind.Should().Be(JsonValueKind.Null, "the guild is tied to no org yet");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("jwt")]
    [InlineData("full-key")]
    [InlineData("admin-static-key")]
    [InlineData("revoked-key")]
    [InlineData("expired-key")]
    [InlineData("banned-owner")]
    public async Task EveryOtherCredential_Is401(string credential)
    {
        var client = await ClientWithAsync(credential, $"ding-401-{credential}");
        var guild = NewSnowflake();

        using var response = await PostSyncAsync(client, guild, Sync(guild, [Member(NewSnowflake(), "refused", joinedAt: LongAgo)], durationMs: 0));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    // --- what a sync records ---

    [Fact]
    public async Task Baseline_RecordsNoGuildEvent_ButReportsTheRenameOfAKnownAccount()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-baseline");
        var (first, second) = (NewSnowflake(), NewSnowflake());
        var (pilot, other, recent) = (NewSnowflake(), NewSnowflake(), NewSnowflake());

        // "recent" joined while this 30-minute collection ran: a baseline still records no arrival.
        var baseline = await PostOkAsync(client, first, Sync(first,
        [
            Member(pilot, "pilote42", joinedAt: LongAgo),
            Member(other, "other", joinedAt: LongAgo),
            Member(recent, "recent", joinedAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
        ], durationMs: BaselineDurationMs));
        var renamed = await PostOkAsync(client, second, Sync(second,
            [Member(pilot, "pilote43", joinedAt: LongAgo)], durationMs: 0));

        baseline.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        ShouldCount(baseline);
        renamed.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        ShouldCount(renamed, nameChanged: 1);
        (await GuildEventsAsync(first)).Should().BeEmpty();
        (await GuildEventsAsync(second)).Should().BeEmpty();
        var rename = (await AccountEventsAsync(pilot)).Should().ContainSingle().Which;
        rename.Type.Should().Be(DiscordEventTypes.UsernameChanged);
        rename.OldValue.Should().Be("pilote42");
        rename.NewValue.Should().Be("pilote43");
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == first))).Should().Be(3);
    }

    [Fact]
    public async Task CompleteSync_RecordsJoinsDeparturesAndRoleNickAndNameChanges()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-diff");
        var guild = NewSnowflake();
        var (officer, pilot) = (NewSnowflake(), NewSnowflake());
        var roles = new[] { Role(officer, "Officier", 12), Role(pilot, "Pilote", 5) };
        var (promoted, renick, rename, leaver, stayer, newcomer) =
            (NewSnowflake(), NewSnowflake(), NewSnowflake(), NewSnowflake(), NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
        [
            Member(promoted, "promoted", [pilot], joinedAt: LongAgo),
            Member(renick, "renick", nick: "Old", joinedAt: LongAgo),
            Member(rename, "rename", globalName: "Old Name", joinedAt: LongAgo),
            Member(leaver, "leaver", joinedAt: LongAgo),
            Member(stayer, "stayer", joinedAt: LongAgo),
        ], roles, durationMs: BaselineDurationMs));
        var joinedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var result = await PostOkAsync(client, guild, Sync(guild,
        [
            Member(promoted, "promoted", [pilot, officer], joinedAt: LongAgo),
            Member(renick, "renick", nick: "New", joinedAt: LongAgo),
            Member(rename, "rename", globalName: "New Name", joinedAt: LongAgo),
            Member(stayer, "stayer", joinedAt: LongAgo),
            Member(newcomer, "newcomer", joinedAt: joinedAt),
        ], roles, durationMs: 0));

        result.GetProperty("isBaseline").GetBoolean().Should().BeFalse();
        result.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        ShouldCount(result, joined: 1, left: 1, rolesChanged: 1, nickChanged: 1, nameChanged: 1);
        var events = await GuildEventsAsync(guild);
        var joined = events.Should().ContainSingle(e => e.Type == DiscordEventTypes.Joined).Which;
        joined.DiscordUserId.Should().Be(newcomer);
        joined.OccurredAt.Should().Be(Second(joinedAt), "an arrival is dated by Discord's JoinedAt");
        joined.NewValue.Should().Be(DiscordFormats.Iso(Second(joinedAt)));
        events.Should().ContainSingle(e => e.Type == DiscordEventTypes.Left).Which.DiscordUserId.Should().Be(leaver);
        var rolesChanged = events.Should().ContainSingle(e => e.Type == DiscordEventTypes.RolesChanged).Which;
        rolesChanged.DiscordUserId.Should().Be(promoted);
        rolesChanged.OldValue.Should().Contain("Pilote").And.NotContain("Officier");
        rolesChanged.NewValue.Should().Contain("Pilote").And.Contain("Officier");
        var nick = events.Should().ContainSingle(e => e.Type == DiscordEventTypes.NickChanged).Which;
        nick.OldValue.Should().Be("Old");
        nick.NewValue.Should().Be("New");
        var globalName = (await AccountEventsAsync(rename)).Should().ContainSingle().Which;
        globalName.Type.Should().Be(DiscordEventTypes.GlobalNameChanged);
        globalName.NewValue.Should().Be("New Name");
        (await DbAsync(db => db.DiscordMembers.SingleAsync(m => m.GuildId == guild && m.DiscordUserId == leaver)))
            .LeftAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeletedRole_IsNotABurstOfRoleChanges_AndIsCounted()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-deleted-role");
        var guild = NewSnowflake();
        var (kept, deleted) = (NewSnowflake(), NewSnowflake());
        var members = new[]
        {
            Member(NewSnowflake(), "both", [kept, deleted], joinedAt: LongAgo),
            Member(NewSnowflake(), "only-deleted-a", [deleted], joinedAt: LongAgo),
            Member(NewSnowflake(), "only-deleted-b", [deleted], joinedAt: LongAgo),
        };
        await PostOkAsync(client, guild, Sync(guild, members, [Role(kept, "Officier", 2), Role(deleted, "Recrue", 1)], durationMs: 0));

        // The role was deleted during the collection: the members still reference it.
        var result = await PostOkAsync(client, guild, Sync(guild, members, [Role(kept, "Officier", 2)], durationMs: 0));

        ShouldCount(result);
        result.GetProperty("unknownRoleRefs").GetInt32().Should().Be(3);
        (await DbAsync(db => db.DiscordRoles.SingleAsync(r => r.GuildId == guild && r.RoleId == deleted)))
            .DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReturningMember_IsARejoin_UnlessTheSameStayProvesTheDepartureWasFalse()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-rejoin");
        var guild = NewSnowflake();
        var (returning, missed) = (NewSnowflake(), NewSnowflake());
        var missedJoin = DateTimeOffset.UtcNow.AddDays(-300);
        var stayers = Enumerable.Range(0, 3).Select(i => Member(NewSnowflake(), $"stayer{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild,
            [.. stayers, Member(returning, "returning", joinedAt: LongAgo), Member(missed, "missed", joinedAt: missedJoin)],
            durationMs: BaselineDurationMs));
        ShouldCount(await PostOkAsync(client, guild, Sync(guild, stayers, durationMs: 0)), left: 2);
        var rejoinedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var result = await PostOkAsync(client, guild, Sync(guild,
            [.. stayers, Member(returning, "returning", joinedAt: rejoinedAt), Member(missed, "missed", joinedAt: missedJoin)],
            durationMs: 0));

        ShouldCount(result, rejoined: 1);
        var events = await GuildEventsAsync(guild);
        events.Where(e => e.DiscordUserId == missed).Should().BeEmpty("the same JoinedAt proves the departure was false");
        var returned = events.Where(e => e.DiscordUserId == returning).ToList();
        returned.Select(e => e.Type).Should().Equal(DiscordEventTypes.Left, DiscordEventTypes.Rejoined);
        returned[1].OccurredAt.Should().Be(Second(rejoinedAt));
        returned[1].OldValue.Should().Be(DiscordFormats.Iso(Second(LongAgo)));
        returned[1].NewValue.Should().Be(DiscordFormats.Iso(Second(rejoinedAt)));
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task ActiveMember_WithALaterJoinedAt_LeftAndCameBack_AnEarlierOneIsIgnored()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-later-join");
        var guild = NewSnowflake();
        var (member, other) = (NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "member", joinedAt: LongAgo), Member(other, "other", joinedAt: LongAgo)], durationMs: 0));
        var later = DateTimeOffset.UtcNow.AddMinutes(-5);

        var rejoined = await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "member", joinedAt: later), Member(other, "other", joinedAt: LongAgo)], durationMs: 0));
        var earlier = await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "member", joinedAt: LongAgo), Member(other, "other", joinedAt: LongAgo)], durationMs: 0));

        ShouldCount(rejoined, rejoined: 1);
        (await GuildEventsAsync(guild)).Should().ContainSingle().Which.OccurredAt.Should().Be(Second(later));
        ShouldCount(earlier);
        (await DbAsync(db => db.DiscordMembers.SingleAsync(m => m.GuildId == guild && m.DiscordUserId == member)))
            .JoinedAt.Should().Be(Second(later));
    }

    [Fact]
    public async Task JoinedAt_SentWithAnotherOffset_IsTheSameInstant()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-offset");
        var guild = NewSnowflake();
        var member = NewSnowflake();
        var utc = new DateTimeOffset(2025, 3, 14, 20, 11, 5, 123, TimeSpan.Zero);
        await PostOkAsync(client, guild, Sync(guild, [Member(member, "offset", joinedAt: utc)], durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild,
            [Member(member, "offset", joinedAt: utc.ToOffset(TimeSpan.FromHours(2)))], durationMs: 0));

        ShouldCount(result);
        (await DbAsync(db => db.DiscordMembers.SingleAsync(m => m.GuildId == guild && m.DiscordUserId == member)))
            .JoinedAt.Should().Be(new DateTime(2025, 3, 14, 20, 11, 5, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(false, "member-search")]
    [InlineData(true, "role-members")]
    [InlineData(true, "cache")]
    public async Task SyncsThatCannotBeComplete_NeverRecordDepartures(bool declaredComplete, string method)
    {
        var (client, _, _) = await IngestClientAsync(factory, $"ding-partial-{method}-{declaredComplete}");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 3).Select(i => Member(NewSnowflake(), $"partial{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild, members.Take(1), complete: declaredComplete, method: method, durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeFalse();
        ShouldCount(result);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt == null))).Should().Be(3);
    }

    [Fact]
    public async Task DeclaredComplete_WithFewerMembersThanExpected_IsPartial()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-mismatch");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 3).Select(i => Member(NewSnowflake(), $"mismatch{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild, members.Take(1), expectedCount: 3, durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeFalse("1 member collected out of 3 announced");
        ShouldCount(result);
        var syncs = await DbAsync(db => db.DiscordSyncs.AsNoTracking().Where(s => s.GuildId == guild).OrderBy(s => s.Id).ToListAsync());
        syncs[1].DeclaredComplete.Should().BeTrue();
        syncs[1].IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task IdenticalRepost_CreatesNoEvent()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-identical");
        var guild = NewSnowflake();
        var role = NewSnowflake();
        var body = Sync(guild,
            [Member(NewSnowflake(), "same", [role], nick: "Same", globalName: "Same", joinedAt: LongAgo)],
            [Role(role, "Membre", 1)], durationMs: 0);
        await PostOkAsync(client, guild, body);

        var again = await PostOkAsync(client, guild, body);

        ShouldCount(again);
        (await GuildEventsAsync(guild)).Should().BeEmpty();
        (await DbAsync(db => db.DiscordSyncs.CountAsync(s => s.GuildId == guild))).Should().Be(2);
    }

    [Fact]
    public async Task MemberFromBeforeTracking_IsStoredWithoutAJoin()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-pre-tracking");
        var guild = NewSnowflake();
        var (known, early, undated) = (NewSnowflake(), NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild, [Member(known, "known", joinedAt: LongAgo)],
            complete: false, method: "role-members", durationMs: 0));

        var result = await PostOkAsync(client, guild, Sync(guild,
        [
            Member(known, "known", joinedAt: LongAgo),
            Member(early, "early", joinedAt: LongAgo),   // joined before the baseline, missed by it
            Member(undated, "undated"),                  // no JoinedAt and no complete sync before
        ], durationMs: 0));

        ShouldCount(result);
        (await GuildEventsAsync(guild)).Should().BeEmpty();
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt == null))).Should().Be(3);
    }

    [Fact]
    public async Task OptedOutMember_IsNeverStored_AndTheSyncStaysComplete()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-optout");
        var guild = NewSnowflake();
        var (kept, optedOut) = (NewSnowflake(), NewSnowflake());
        await DbAsync(db =>
        {
            db.DiscordOptOuts.Add(new DiscordOptOut { DiscordUserId = optedOut, CreatedAt = DateTime.UtcNow, ByUsername = "admin" });
            return db.SaveChangesAsync();
        });

        var result = await PostOkAsync(client, guild, Sync(guild,
            [Member(kept, "kept", joinedAt: LongAgo), Member(optedOut, "optedout", joinedAt: LongAgo)], durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeTrue("completeness is judged on the message as sent");
        result.GetProperty("membersReceived").GetInt32().Should().Be(2);
        result.GetProperty("membersOptedOut").GetInt32().Should().Be(1);
        (await DbAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == optedOut))).Should().BeFalse();
        (await DbAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == optedOut))).Should().BeFalse();
        (await DbAsync(db => db.DiscordSyncs.SingleAsync(s => s.GuildId == guild))).OptedOutCount.Should().Be(1);
    }

    private sealed class InterruptFirstMemberDeletion : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
                && command.CommandText.Contains("discord_members", StringComparison.Ordinal))
                throw new InvalidOperationException("interrupted account erasure");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task InterruptedAccountErasure_AbsentOptedOutMember_IsNeitherTouchedNorDeparted()
    {
        var (client, _, actorId) = await IngestClientAsync(factory, "ding-interrupted-optout");
        var guild = NewSnowflake();
        var (excluded, kept) = (NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
            [Member(excluded, "excluded", nick: "Original", joinedAt: LongAgo), Member(kept, "kept", joinedAt: LongAgo)],
            durationMs: BaselineDurationMs));
        var before = await DbAsync(db => db.DiscordMembers.AsNoTracking().SingleAsync(m => m.GuildId == guild && m.DiscordUserId == excluded));
        var accountBefore = await DbAsync(db => db.DiscordAccounts.AsNoTracking().SingleAsync(a => a.DiscordUserId == excluded));
        await using (var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(factory.DataDir, "tracker.db")};Pooling=False")
            .AddInterceptors(new InterruptFirstMemberDeletion()).Options))
        {
            var act = () => new DiscordErasureRepository(db).EraseAccountAsync(excluded, actorId, "admin");
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        (await DbAsync(db => db.DiscordOptOuts.AnyAsync(o => o.DiscordUserId == excluded))).Should().BeTrue();

        var result = await PostOkAsync(client, guild, Sync(guild, [Member(kept, "kept", joinedAt: LongAgo)], durationMs: 0));

        result.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        result.GetProperty("massDepartureDetected").GetBoolean().Should().BeFalse();
        result.GetProperty("membersOptedOut").GetInt32().Should().Be(0, "the excluded account is absent from the raw payload");
        ShouldCount(result);
        var after = await DbAsync(db => db.DiscordMembers.AsNoTracking().SingleAsync(m => m.GuildId == guild && m.DiscordUserId == excluded));
        after.Should().BeEquivalentTo(before, "an opted-out member is ignored even when an interrupted erasure left its row");
        (await DbAsync(db => db.DiscordAccounts.AsNoTracking().SingleAsync(a => a.DiscordUserId == excluded)))
            .Should().BeEquivalentTo(accountBefore);
        (await DbAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.DiscordUserId == excluded))).Should().BeFalse();
    }

    [Fact]
    public async Task MassDeparture_IsRecordedAndSignalled_WithoutAdministratorAuthorization()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-mass-signal");
        var guild = NewSnowflake();
        var members = Enumerable.Range(0, 40).Select(i => Member(NewSnowflake(), $"mass{i}", joinedAt: LongAgo)).ToList();
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));
        var remaining = members.Take(28).ToList();

        var accepted = await PostOkAsync(client, guild, Sync(guild, remaining, durationMs: 0));

        accepted.GetProperty("massDepartureDetected").GetBoolean().Should().BeTrue();
        accepted.GetProperty("departureGuardTripped").GetBoolean().Should().BeFalse("older installed plugins must not report blocked departures");
        accepted.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        ShouldCount(accepted, left: 12);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt != null))).Should().Be(12);
        (await DbAsync(db => db.DiscordMemberEvents.CountAsync(e => e.GuildId == guild && e.Type == DiscordEventTypes.Left))).Should().Be(12);

        var repeated = await PostOkAsync(client, guild, Sync(guild, remaining, durationMs: 0));
        repeated.GetProperty("massDepartureDetected").GetBoolean().Should().BeFalse();
        repeated.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        ShouldCount(repeated);
        (await DbAsync(db => db.DiscordSyncs.Where(s => s.GuildId == guild).OrderBy(s => s.Id)
            .Select(s => s.MassDepartureDetected).ToListAsync())).Should().Equal(false, true, false);
    }

    // --- refusals ---

    [Fact]
    public async Task EmptyCompleteSync_Is400EmptyCompleteSync()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-empty");
        var guild = NewSnowflake();

        using var response = await PostSyncAsync(client, guild, Sync(guild, [], durationMs: 0));

        (await ProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("code").GetString().Should().Be("empty_complete_sync");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    [Fact]
    public async Task SyncCollectedBeforeTheLastAcceptedOne_Is409Stale()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-stale");
        var guild = NewSnowflake();
        var members = new[] { Member(NewSnowflake(), "stale-a", joinedAt: LongAgo), Member(NewSnowflake(), "stale-b", joinedAt: LongAgo) };
        await PostOkAsync(client, guild, Sync(guild, members, durationMs: 0));

        // This collection started 10 min ago, before the baseline was received.
        using var response = await PostSyncAsync(client, guild, Sync(guild, members.Take(1), durationMs: 600_000));

        (await ProblemAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("stale_sync");
        (await DbAsync(db => db.DiscordSyncs.CountAsync(s => s.GuildId == guild))).Should().Be(1);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild && m.LeftAt != null))).Should().Be(0);
    }

    [Fact]
    public async Task ExcludedGuild_Is409GuildExcluded()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-excluded");
        var guild = NewSnowflake();
        await DbAsync(db =>
        {
            db.DiscordGuildOptOuts.Add(new DiscordGuildOptOut { GuildId = guild, CreatedAt = DateTime.UtcNow, ByUsername = "admin" });
            return db.SaveChangesAsync();
        });

        using var response = await PostSyncAsync(client, guild, Sync(guild, [Member(NewSnowflake(), "excluded", joinedAt: LongAgo)], durationMs: 0));

        (await ProblemAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("guild_excluded");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    [Theory]
    [InlineData("route-not-snowflake")]
    [InlineData("guild-id-mismatch")]
    [InlineData("duplicate-user-id")]
    [InlineData("invalid-icon")]
    public async Task InvalidSync_Is400InvalidSync_AndNothingIsStored(string rule)
    {
        var (client, _, _) = await IngestClientAsync(factory, $"ding-400-{rule}");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var valid = Sync(guild, [Member(user, "invalid", joinedAt: LongAgo)], durationMs: 0);
        var (route, body) = rule switch
        {
            "route-not-snowflake" => ("not-a-snowflake", valid),
            "guild-id-mismatch" => (NewSnowflake(), valid),
            "duplicate-user-id" => (guild, valid with
            {
                Members = [Member(user, "first", joinedAt: LongAgo), Member(user, "second", joinedAt: LongAgo)],
                Coverage = valid.Coverage with { ExpectedCount = 2, CollectedCount = 2 },
            }),
            "invalid-icon" => (guild, valid with { Guild = valid.Guild with { Icon = "../../avatars/x.png" } }),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
        };

        using var response = await PostSyncAsync(client, route, body);

        (await ProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("code").GetString().Should().Be("invalid_sync");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild || g.GuildId == route))).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownFields_AndNullOptionalFields_AreAccepted()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-nulls");
        var guild = NewSnowflake();
        var user = NewSnowflake();
        var json = $$"""
            {
              "pluginVersion": "1.0.0",
              "collectedAt": "2026-09-30T12:00:00Z",
              "collectionDurationMs": 0,
              "sentByALaterPlugin": { "extra": [1, 2, 3] },
              "guild": { "id": "{{guild}}", "name": "Nulls", "icon": null, "memberCount": null, "banner": null },
              "coverage": { "method": "member-search", "complete": true, "expectedCount": null, "collectedCount": 1 },
              "roles": [],
              "members": [
                { "userId": "{{user}}", "username": "nulls", "globalName": null, "nick": null,
                  "roleIds": [], "joinedAt": null, "bot": false, "avatar": "ignored" }
              ]
            }
            """;

        using var response = await client.PostAsync($"/api/ingest/discord/guilds/{guild}/syncs",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("isComplete").GetBoolean().Should().BeFalse("without an expected count nothing proves the list is whole");
        result.GetProperty("membersReceived").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("roles", true)]
    [InlineData("roles", false)]
    [InlineData("members", true)]
    [InlineData("members", false)]
    public async Task MissingOrNullRequiredArray_Is400_Never500(string field, bool presentAsNull)
    {
        var (client, _, _) = await IngestClientAsync(factory, $"ding-array-{field}-{presentAsNull}");
        var guild = NewSnowflake();
        var body = JsonSerializer.SerializeToNode(
            Sync(guild, [Member(NewSnowflake(), "arrays", joinedAt: LongAgo)], durationMs: 0), JsonSerializerOptions.Web)!.AsObject();
        if (presentAsNull) body[field] = null;
        else body.Remove(field);

        using var response = await client.PostAsync($"/api/ingest/discord/guilds/{guild}/syncs",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await DbAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == guild))).Should().BeFalse();
    }

    // --- retries and clocks ---

    [Fact]
    public async Task RepostOfACommittedSync_IsStaleOrEventless_NeverDuplicated()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var (_, rawKey, _) = await IngestClientAsync(factory, "ding-repost");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        var guild = NewSnowflake();
        var (a, b, c) = (NewSnowflake(), NewSnowflake(), NewSnowflake());
        await PostOkAsync(client, guild, Sync(guild,
            [Member(a, "repost-a", joinedAt: LongAgo), Member(b, "repost-b", joinedAt: LongAgo), Member(c, "repost-c", joinedAt: LongAgo)],
            durationMs: 0));

        clock.Now += TimeSpan.FromMinutes(10);
        var changed = Sync(guild,
            [Member(a, "repost-a", nick: "Renamed", joinedAt: LongAgo), Member(b, "repost-b", joinedAt: LongAgo)],
            durationMs: 60_000);
        ShouldCount(await PostOkAsync(client, guild, changed), left: 1, nickChanged: 1);
        var events = (await GuildEventsAsync(guild)).Count;

        // The plugin got no answer after its upload and retries at once, within its collection time.
        clock.Now += TimeSpan.FromSeconds(5);
        using (var retry = await PostSyncAsync(client, guild, changed))
        {
            (await ProblemAsync(retry, HttpStatusCode.Conflict)).GetProperty("code").GetString().Should().Be("stale_sync");
        }

        // A later retry of the same body: accepted, and nothing left to record.
        clock.Now += TimeSpan.FromMinutes(10);
        ShouldCount(await PostOkAsync(client, guild, changed));
        (await GuildEventsAsync(guild)).Should().HaveCount(events);
        (await DbAsync(db => db.DiscordSyncs.CountAsync(s => s.GuildId == guild))).Should().Be(3);
    }

    [Fact]
    public async Task PluginClockOffByDays_NeitherRefusesNorReordersSyncs()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-clock");
        var guild = NewSnowflake();
        var (a, b) = (NewSnowflake(), NewSnowflake());
        var ahead = DateTimeOffset.UtcNow.AddDays(5);
        var behind = DateTimeOffset.UtcNow.AddDays(-30);

        await PostOkAsync(client, guild, Sync(guild,
            [Member(a, "clock-a", joinedAt: LongAgo), Member(b, "clock-b", joinedAt: LongAgo)], durationMs: 0) with { CollectedAt = ahead });
        var second = await PostOkAsync(client, guild, Sync(guild,
            [Member(a, "clock-a", joinedAt: LongAgo)], durationMs: 0) with { CollectedAt = behind });

        ShouldCount(second, left: 1);
        var syncs = await DbAsync(db => db.DiscordSyncs.AsNoTracking().Where(s => s.GuildId == guild).OrderBy(s => s.Id).ToListAsync());
        syncs[0].DeclaredCollectedAt.Should().BeCloseTo(ahead.UtcDateTime, TimeSpan.FromSeconds(1));
        syncs[1].DeclaredCollectedAt.Should().BeCloseTo(behind.UtcDateTime, TimeSpan.FromSeconds(1));
        syncs.Should().AllSatisfy(s => s.CollectedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1)));
        syncs[1].CollectedAt.Should().BeOnOrAfter(syncs[0].CollectedAt);
    }

    // --- contention, load and transport ---

    [Fact]
    public async Task TwoSimultaneousSyncs_AreSerialised_AndShareOneAccountRow()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-concurrent");
        var shared = NewSnowflake();
        var guilds = new[] { NewSnowflake(), NewSnowflake() };

        var responses = await Task.WhenAll(guilds.Select(g => PostSyncAsync(client, g, Sync(g,
            [Member(shared, "shared", joinedAt: LongAgo), Member(NewSnowflake(), $"own{g}", joinedAt: LongAgo)], durationMs: 0))));

        foreach (var response in responses)
            response.StatusCode.Should().BeOneOf(new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable },
                await response.Content.ReadAsStringAsync());
        responses.Should().Contain(r => r.StatusCode == HttpStatusCode.OK, "the second one waits for the gate");
        foreach (var busy in responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable))
        {
            busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        }
        (await DbAsync(db => db.DiscordAccounts.CountAsync(a => a.DiscordUserId == shared))).Should().Be(1);
    }

    [Fact]
    public async Task GateHeldLongerThan10s_Is503WithRetryAfter_ThenARetrySucceeds()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-gate-busy");
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "waiting", joinedAt: LongAgo)], durationMs: 0);
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();

        using (await gate.EnterAsync(CancellationToken.None))
        {
            using var busy = await PostSyncAsync(client, guild, body);
            (await ProblemAsync(busy, HttpStatusCode.ServiceUnavailable)).GetProperty("code").GetString().Should().Be("busy");
            busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        }

        (await PostOkAsync(client, guild, body)).GetProperty("isBaseline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TrackerDbLockedByAnotherWriter_Is503Busy_ThenARetrySucceeds()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-db-busy");
        client.Timeout = TimeSpan.FromMinutes(5);   // busy_timeout, one retry after 2 s, busy_timeout again
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "locked", joinedAt: LongAgo)], durationMs: 0);

        // Plays the collector: holds the write lock of tracker.db until the API has given up.
        await using (var collector = new SqliteConnection($"Data Source={Path.Combine(factory.DataDir, "tracker.db")};Pooling=False"))
        {
            await collector.OpenAsync();
            await using (var begin = collector.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                using var busy = await PostSyncAsync(client, guild, body);
                (await ProblemAsync(busy, HttpStatusCode.ServiceUnavailable)).GetProperty("code").GetString().Should().Be("busy");
                busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
            }
            finally
            {
                await using var rollback = collector.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync();
            }
        }

        (await PostOkAsync(client, guild, body)).GetProperty("isBaseline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FiftyThousandMemberBaseline_IsStoredInFull()
    {
        var (client, _, _) = await IngestClientAsync(factory, "ding-50k");
        client.Timeout = TimeSpan.FromMinutes(5);
        var guild = NewSnowflake();
        var role = NewSnowflake();
        var members = Enumerable.Range(0, DiscordSyncValidator.MaxMembers)
            .Select(i => Member(NewSnowflake(), $"member{i}", [role], joinedAt: LongAgo))
            .ToList();

        var result = await PostOkAsync(client, guild, Sync(guild, members, [Role(role, "Membre", 1)], durationMs: 0));

        result.GetProperty("isBaseline").GetBoolean().Should().BeTrue();
        result.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        result.GetProperty("membersReceived").GetInt32().Should().Be(DiscordSyncValidator.MaxMembers);
        (await DbAsync(db => db.DiscordMembers.CountAsync(m => m.GuildId == guild))).Should().Be(DiscordSyncValidator.MaxMembers);
        (await DbAsync(db => db.DiscordSyncs.SingleAsync(s => s.GuildId == guild))).CollectedCount.Should().Be(DiscordSyncValidator.MaxMembers);
    }

    [Fact]
    public async Task BodyOverTheLimit_Is413ProblemDetails()
    {
        // TestServer does not enforce [RequestSizeLimit]: this test needs a real Kestrel server.
        var (_, rawKey, _) = await IngestClientAsync(factory, "ding-413");
        using var kestrel = new KestrelFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        var address = kestrel.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        // Kestrel refuses the body from its Content-Length, before it sends "100 Continue". The
        // client waits for that answer instead of uploading, so the refusal never races the upload.
        using var handler = new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(30) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        var warmUp = NewSnowflake();
        (await PostSyncAsync(client, warmUp, Sync(warmUp, [Member(NewSnowflake(), "warm-up", joinedAt: LongAgo)], durationMs: 0)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // Over the action's 25 MiB, under Kestrel's own 30,000,000-byte default: only
        // [RequestSizeLimit] can refuse it.
        var padding = new string('x', (int)DiscordIngestController.MaxBodyBytes + 1024 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/ingest/discord/guilds/{NewSnowflake()}/syncs")
        {
            Content = new StringContent($$"""{"pluginVersion":"{{padding}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var problem = await ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
        problem.GetProperty("status").GetInt32().Should().Be(413);
        problem.GetProperty("title").GetString().Should().Be("Payload Too Large");
        problem.TryGetProperty("detail", out _).Should().BeFalse("the framework message is only logged");
        problem.TryGetProperty("code", out _).Should().BeFalse("a 413 carries no Discord code");
    }

    [Fact]
    public async Task IngestBudget_Is429WithRetryAfter()
    {
        var (_, rawKey, _) = await IngestClientAsync(factory, "ding-429");
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<RateLimitSettings>(limits => limits.DiscordIngest.PermitLimit = 2)));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        var guild = NewSnowflake();
        var body = Sync(guild, [Member(NewSnowflake(), "limited", joinedAt: LongAgo)], durationMs: 0);

        (await PostSyncAsync(client, guild, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostSyncAsync(client, guild, body)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var limited = await PostSyncAsync(client, guild, body);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        limited.Headers.RetryAfter!.Delta.Should().BePositive();
    }

    // --- helpers ---

    /// <summary>A clock the test moves by hand; only <c>GetUtcNow</c> is used by the ingest route.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The real API on Kestrel. It reuses the data directory and secrets the shared ApiFactory
    /// put in the environment.
    /// </summary>
    private sealed class KestrelFactory : WebApplicationFactory<Program>
    {
    }

    private static DateTime Second(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string guildId, DiscordSyncRequest body)
    {
        using var response = await PostSyncAsync(client, guildId, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void ShouldCount(JsonElement result, int joined = 0, int left = 0, int rejoined = 0,
        int rolesChanged = 0, int nickChanged = 0, int nameChanged = 0)
    {
        var events = result.GetProperty("events");
        var actual = new[] { "joined", "left", "rejoined", "rolesChanged", "nickChanged", "nameChanged" }
            .ToDictionary(name => name, name => events.GetProperty(name).GetInt32());
        actual.Should().Equal(new Dictionary<string, int>
        {
            ["joined"] = joined,
            ["left"] = left,
            ["rejoined"] = rejoined,
            ["rolesChanged"] = rolesChanged,
            ["nickChanged"] = nickChanged,
            ["nameChanged"] = nameChanged,
        });
    }

    private async Task<HttpClient> ClientWithAsync(string credential, string username)
    {
        switch (credential)
        {
            case "none":
                return factory.CreateClient();
            case "jwt":
                return await factory.SignedInClientAsync(username);
            case "admin-static-key":
                return KeyClient(ApiFactory.AdminApiKey);
            case "full-key":
            {
                var owner = await factory.SignedInClientAsync(username);
                var created = await owner.PostAsJsonAsync("/api/api-keys", new { name = "full", expiresAt = (DateTime?)null });
                created.EnsureSuccessStatusCode();
                return KeyClient((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!);
            }
            case "revoked-key":
            {
                var owner = await factory.SignedInClientAsync(username);
                var created = await owner.PostAsJsonAsync("/api/api-keys",
                    new { name = "revoked", expiresAt = DateTime.UtcNow.AddDays(30), scope = "discord:ingest" });
                created.EnsureSuccessStatusCode();
                var key = await created.Content.ReadFromJsonAsync<JsonElement>();
                (await owner.DeleteAsync($"/api/api-keys/{key.GetProperty("id").GetInt64()}")).EnsureSuccessStatusCode();
                return KeyClient(key.GetProperty("rawKey").GetString()!);
            }
            case "expired-key":
            {
                var (client, _, userId) = await IngestClientAsync(factory, username);
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ApiKeys
                    .Where(k => k.ApiUserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, (DateTime?)DateTime.UtcNow.AddMinutes(-1)));
                return client;
            }
            case "banned-owner":
            {
                var (client, _, userId) = await IngestClientAsync(factory, username);
                using var admin = KeyClient(ApiFactory.AdminApiKey);
                (await admin.PutAsJsonAsync($"/api/admin/users/{userId}", new { isBanned = true })).EnsureSuccessStatusCode();
                return client;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(credential), credential, null);
        }
    }

    private HttpClient KeyClient(string rawKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        return client;
    }

    private async Task<T> DbAsync<T>(Func<TrackerDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task<List<DiscordMemberEvent>> GuildEventsAsync(string guildId) =>
        DbAsync(db => db.DiscordMemberEvents.AsNoTracking().Where(e => e.GuildId == guildId).OrderBy(e => e.Id).ToListAsync());

    private Task<List<DiscordMemberEvent>> AccountEventsAsync(string userId) =>
        DbAsync(db => db.DiscordMemberEvents.AsNoTracking()
            .Where(e => e.GuildId == null && e.DiscordUserId == userId).OrderBy(e => e.Id).ToListAsync());
}
