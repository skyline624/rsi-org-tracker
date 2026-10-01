using System.Globalization;
using Collector.Discord;
using Collector.Models;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>Spec § 9.3, one fact per rule: what a sync writes and which events it records.</summary>
public sealed class DiscordRosterDiffTests
{
    private const string Guild = "100000000000000001";
    private const string RoleA = "300000000000000001";
    private const string RoleB = "300000000000000002";
    private const string RoleC = "300000000000000003";

    private static readonly DateTime FirstSync = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastSeen = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Before = new(2025, 3, 14, 20, 11, 5, DateTimeKind.Utc);
    private static readonly DateTime After = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LeftAt = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> NoOptOut = new HashSet<string>();

    private static string User(int n) => (200000000000000000L + n).ToString(CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) => DiscordFormats.Iso(utc);

    private static NormalizedRole Role(string id, string name, int position = 1, bool hoist = true, bool managed = false) =>
        new(id, name, position, null, hoist, managed);

    private static NormalizedMember Member(int n, string? nick = null, string[]? roles = null, DateTime? joinedAt = null,
        string? username = null, string? globalName = null, bool bot = false) =>
        new(User(n), username ?? $"user{n}", globalName, nick, (roles ?? []).Order(StringComparer.Ordinal).ToList(), joinedAt, bot);

    /// <summary>A member-search sync; complete unless said otherwise. Roles default to RoleA alone.</summary>
    private static NormalizedSync Sync(IEnumerable<NormalizedMember> members, IEnumerable<NormalizedRole>? roles = null, bool complete = true)
    {
        var list = members.ToList();
        return new NormalizedSync(Guild, "Ma Corpo", null, list.Count, DiscordSyncMethods.MemberSearch,
            complete, complete, list.Count, list.Count, "1.0.0", Now, TimeSpan.FromMinutes(1),
            (roles ?? [Role(RoleA, "Recrue")]).ToList(), list, 0);
    }

    private static MemberSnapshot Stored(int n, string? nick = null, string[]? roles = null, DateTime? joinedAt = null,
        DateTime? leftAt = null, long? lastLeftEventId = null) =>
        new(User(n), nick, (roles ?? []).Order(StringComparer.Ordinal).ToList(), joinedAt, LastSeen, leftAt, lastLeftEventId);

    /// <summary>A tracked server (baseline taken at FirstSync). Roles default to RoleA alone.</summary>
    private static RosterSnapshot Snapshot(IEnumerable<MemberSnapshot>? members = null, IEnumerable<RoleSnapshot>? roles = null,
        IEnumerable<AccountSnapshot>? accounts = null, DateTime? lastCompleteSyncAt = null) =>
        new(new GuildSnapshot(FirstSync, lastCompleteSyncAt),
            (roles ?? [new RoleSnapshot(RoleA, "Recrue", false)]).ToDictionary(r => r.RoleId),
            (members ?? []).ToDictionary(m => m.UserId),
            (accounts ?? []).ToDictionary(a => a.UserId));

    /// <summary>A server never synced before; accounts may already be known from other servers.</summary>
    private static RosterSnapshot Untracked(IEnumerable<AccountSnapshot>? accounts = null) =>
        new(null, new Dictionary<string, RoleSnapshot>(), new Dictionary<string, MemberSnapshot>(),
            (accounts ?? []).ToDictionary(a => a.UserId));

    private static RosterPlan Compute(NormalizedSync sync, RosterSnapshot snapshot, IReadOnlySet<string>? optedOut = null) =>
        DiscordRosterDiff.Compute(Guild, sync, Now, snapshot, optedOut ?? NoOptOut);

    // ── Baseline ──

    [Fact]
    public void ABaseline_IsReported_AndRecordsNoServerEvent()
    {
        var plan = Compute(Sync([Member(1, roles: [RoleA]), Member(2)]), Untracked());

        plan.IsBaseline.Should().BeTrue();
        plan.IsComplete.Should().BeTrue();
        plan.Events.Should().BeEmpty();
        plan.RolesToInsert.Select(r => r.RoleId).Should().Equal(RoleA);
        plan.AccountsToInsert.Select(a => a.UserId).Should().Equal(User(1), User(2));
        plan.PresentUserIds.Should().Equal(User(1), User(2));
    }

    [Fact]
    public void ABaseline_StillReportsTheRenameOfAnAccountKnownFromAnotherServer()
    {
        var plan = Compute(Sync([Member(1, username: "newname", globalName: "New")]),
            Untracked([new AccountSnapshot(User(1), "oldname", "Old")]));

        plan.IsBaseline.Should().BeTrue();
        plan.Events.Should().Equal(
            new PlannedEvent(null, User(1), DiscordEventTypes.UsernameChanged, "oldname", "newname", null, null, Now),
            new PlannedEvent(null, User(1), DiscordEventTypes.GlobalNameChanged, "Old", "New", null, null, Now));
        plan.AccountsToUpdate.Should().Equal(new AccountWrite(User(1), "newname", "New", false));
        plan.AccountsToInsert.Should().BeEmpty();
    }

    // ── Roles ──

    [Fact]
    public void NewRoles_AreInserted_AndHoistedUnmanagedOnesStartAsRanksAtTheirPosition()
    {
        var officer = Role(RoleB, "Officier", position: 12);
        var bot = Role(RoleC, "Bot", position: 20, managed: true);
        var pings = Role("300000000000000004", "Pings", position: 3, hoist: false);

        var plan = Compute(Sync([Member(1)], [Role(RoleA, "Recrue"), officer, bot, pings]), Snapshot());

        plan.RolesToInsert.Should().Equal(officer, bot, pings);
        plan.RolesToUpdate.Select(r => r.RoleId).Should().Equal(RoleA);
        DiscordRosterDiff.DefaultIsRank(officer).Should().BeTrue();
        DiscordRosterDiff.DefaultRankOrder(officer).Should().Be(12);
        DiscordRosterDiff.DefaultIsRank(bot).Should().BeFalse("an integration role is never a rank");
        DiscordRosterDiff.DefaultRankOrder(bot).Should().BeNull();
        DiscordRosterDiff.DefaultIsRank(pings).Should().BeFalse("a role not listed apart is not a rank");
        DiscordRosterDiff.DefaultRankOrder(pings).Should().BeNull();
    }

    [Fact]
    public void KnownRoles_AreAlwaysUpdatedFromThePayload()
    {
        var renamed = Role(RoleA, "Membre", position: 5, hoist: false);

        var plan = Compute(Sync([Member(1)], [renamed]), Snapshot());

        plan.RolesToUpdate.Should().Equal(renamed);
        plan.RolesToInsert.Should().BeEmpty();
        plan.RoleIdsToMarkDeleted.Should().BeEmpty();
    }

    [Fact]
    public void MissingRoles_AreMarkedDeletedOnce_EvenByAPartialSync()
    {
        var snapshot = Snapshot(roles: [
            new RoleSnapshot(RoleA, "Recrue", false),
            new RoleSnapshot(RoleB, "Officier", false),
            new RoleSnapshot(RoleC, "Ancien", true)]);

        var plan = Compute(Sync([Member(1)], [Role(RoleA, "Recrue")], complete: false), snapshot);

        plan.RoleIdsToMarkDeleted.Should().Equal(RoleB);
    }

    [Fact]
    public void ADeletedRoleThatComesBack_IsRestoredThroughTheUpdates()
    {
        var snapshot = Snapshot(roles: [new RoleSnapshot(RoleA, "Recrue", false), new RoleSnapshot(RoleB, "Officier", true)]);

        var plan = Compute(Sync([Member(1)], [Role(RoleA, "Recrue"), Role(RoleB, "Officier")]), snapshot);

        plan.RolesToUpdate.Select(r => r.RoleId).Should().Equal(RoleA, RoleB);
        plan.RolesToInsert.Should().BeEmpty();
        plan.RoleIdsToMarkDeleted.Should().BeEmpty();
    }

    // ── Accounts ──

    [Fact]
    public void ANewAccount_IsInsertedWithoutEvent()
    {
        var plan = Compute(Sync([Member(1, globalName: "Pilote", bot: true)]), Snapshot());

        plan.AccountsToInsert.Should().Equal(new AccountWrite(User(1), "user1", "Pilote", true));
        plan.AccountsToUpdate.Should().BeEmpty();
        plan.Events.Should().BeEmpty();
    }

    [Fact]
    public void AccountRenames_AreAccountEvents_WithoutAServer()
    {
        var accounts = new[]
        {
            new AccountSnapshot(User(1), "user1", "Pilote"),
            new AccountSnapshot(User(2), "old2", null),
            new AccountSnapshot(User(3), "user3", null),
        };

        var plan = Compute(Sync([Member(1), Member(2), Member(3)]), Snapshot([Stored(1), Stored(2), Stored(3)], accounts: accounts));

        plan.Events.Should().Equal(
            new PlannedEvent(null, User(1), DiscordEventTypes.GlobalNameChanged, "Pilote", null, null, null, Now),
            new PlannedEvent(null, User(2), DiscordEventTypes.UsernameChanged, "old2", "user2", null, null, Now));
        plan.AccountsToUpdate.Should().Equal(
            new AccountWrite(User(1), "user1", null, false),
            new AccountWrite(User(2), "user2", null, false));
        plan.AccountsToInsert.Should().BeEmpty();
    }

    // ── Opt-outs ──

    [Fact]
    public void OptedOutMembers_AreDroppedAndCounted_AndTheSyncStaysComplete()
    {
        var plan = Compute(Sync([Member(1), Member(2)]), Snapshot(), new HashSet<string> { User(2) });

        plan.OptedOutCount.Should().Be(1);
        plan.IsComplete.Should().BeTrue("completeness is judged on the message as sent");
        plan.AccountsToInsert.Select(a => a.UserId).Should().Equal(User(1));
        plan.PresentUserIds.Should().Equal(User(1));
        plan.Events.Should().BeEmpty();
    }

    /// <summary>MemberWrite is a record holding a list: compare the list's content, not its reference.</summary>
    private static void ShouldBe(MemberWrite actual, MemberWrite expected) =>
        actual.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<MemberWrite>());

    /// <summary>What the repository stores once <paramref name="sync"/> is applied.</summary>
    private static RosterSnapshot SnapshotAfter(NormalizedSync sync) =>
        new(new GuildSnapshot(FirstSync, Now, false),
            sync.Roles.ToDictionary(r => r.RoleId, r => new RoleSnapshot(r.RoleId, r.Name, false)),
            sync.Members.ToDictionary(m => m.UserId, m => new MemberSnapshot(m.UserId, m.Nick, m.RoleIds, m.JoinedAt, Now, null, null)),
            sync.Members.ToDictionary(m => m.UserId, m => new AccountSnapshot(m.UserId, m.Username, m.GlobalName)));

    // ── Baseline members ──

    [Fact]
    public void ABaseline_InsertsEveryMemberAsFound_WithoutArrivals()
    {
        var plan = Compute(Sync([Member(1, joinedAt: Now.AddDays(-1)), Member(2, roles: [RoleA], joinedAt: Before), Member(3)]), Untracked());

        plan.Events.Should().BeEmpty();
        plan.MembersToInsert.Select(m => m.UserId).Should().Equal(User(1), User(2), User(3));
        ShouldBe(plan.MembersToInsert[1], new MemberWrite(User(2), null, [RoleA], Before, null));
        plan.MembersToUpdate.Should().BeEmpty();
        plan.MassDepartureDetected.Should().BeFalse();
    }

    // ── Known, active member ──

    [Fact]
    public void ChangedRoles_AreLogged_WithTheNamesOfTheTime_OrderedById()
    {
        var snapshot = Snapshot([Stored(1, roles: [RoleB])],
            roles: [new RoleSnapshot(RoleA, "Recrue", false), new RoleSnapshot(RoleB, "Membre", false)]);

        var plan = Compute(Sync([Member(1, roles: [RoleB, RoleA])], [Role(RoleB, "Membre confirmé"), Role(RoleA, "Recrue")]), snapshot);

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.RolesChanged,
            $$"""[{"id":"{{RoleB}}","name":"Membre"}]""",
            $$"""[{"id":"{{RoleA}}","name":"Recrue"},{"id":"{{RoleB}}","name":"Membre confirmé"}]""",
            null, LastSeen, Now));
        plan.MembersToUpdate.Should().ContainSingle().Which.RoleIds.Should().Equal(RoleA, RoleB);
    }

    [Fact]
    public void AChangedNick_IsLogged()
    {
        var plan = Compute(Sync([Member(1, nick: "[CORP] Pilote")]), Snapshot([Stored(1, nick: "Pilote")]));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.NickChanged, "Pilote", "[CORP] Pilote", null, LastSeen, Now));
        plan.MembersToUpdate.Should().ContainSingle().Which.Nick.Should().Be("[CORP] Pilote");
    }

    [Fact]
    public void AJoinDateMovedForwardByMoreThanASecond_IsARejoin()
    {
        var plan = Compute(Sync([Member(1, joinedAt: After)]), Snapshot([Stored(1, joinedAt: Before)]));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.Rejoined,
            "2025-03-14T20:11:05Z", "2026-09-25T08:00:00Z", After, null, Now));
        plan.MembersToUpdate.Should().ContainSingle().Which.JoinedAt.Should().Be(After);
    }

    [Theory]
    [InlineData(1)]       // one second later: rounding, not a rejoin
    [InlineData(-86400)]  // earlier: ignored
    public void AJoinDateWithinASecond_OrEarlier_IsIgnored(int seconds)
    {
        var plan = Compute(Sync([Member(1, joinedAt: Before.AddSeconds(seconds))]), Snapshot([Stored(1, joinedAt: Before)]));

        plan.Events.Should().BeEmpty();
        plan.MembersToUpdate.Should().BeEmpty();
    }

    [Fact]
    public void AMissingJoinDate_NeverOverwritesAKnownOne()
    {
        var plan = Compute(Sync([Member(1)]), Snapshot([Stored(1, joinedAt: Before)]));

        plan.Events.Should().BeEmpty();
        plan.MembersToUpdate.Should().BeEmpty();
    }

    [Fact]
    public void AStoredMissingJoinDate_IsCompletedSilently()
    {
        var plan = Compute(Sync([Member(1, joinedAt: Before)]), Snapshot([Stored(1)]));

        plan.Events.Should().BeEmpty();
        plan.MembersToUpdate.Should().ContainSingle().Which.JoinedAt.Should().Be(Before);
    }

    // ── Known member marked as gone ──

    [Fact]
    public void AFalseDeparture_IsUndone_WithoutRejoined()
    {
        var plan = Compute(Sync([Member(1, joinedAt: Before)]), Snapshot([Stored(1, joinedAt: Before, leftAt: LeftAt, lastLeftEventId: 77)]));

        plan.Events.Should().BeEmpty();
        plan.EventIdsToDelete.Should().Equal(77L);
        ShouldBe(plan.MembersToUpdate.Should().ContainSingle().Subject, new MemberWrite(User(1), null, [], Before, null));
    }

    [Fact]
    public void AFalseDeparture_IsTheSameStay_SoItsChangesAreStillLogged()
    {
        var plan = Compute(Sync([Member(1, nick: "Nouveau", joinedAt: Before.AddSeconds(-30))]),
            Snapshot([Stored(1, nick: "Ancien", joinedAt: Before, leftAt: LeftAt, lastLeftEventId: 77)]));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.NickChanged, "Ancien", "Nouveau", null, LastSeen, Now));
        plan.EventIdsToDelete.Should().Equal(77L);
        ShouldBe(plan.MembersToUpdate.Should().ContainSingle().Subject, new MemberWrite(User(1), "Nouveau", [], Before, null));
    }

    [Fact]
    public void AReturnWithALaterJoinDate_IsARejoinAtThatDate()
    {
        var plan = Compute(Sync([Member(1, nick: "Nouveau", joinedAt: After)]),
            Snapshot([Stored(1, nick: "Ancien", joinedAt: Before, leftAt: LeftAt, lastLeftEventId: 77)]));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.Rejoined, Iso(Before), Iso(After), After, null, Now));
        plan.EventIdsToDelete.Should().BeEmpty();
        ShouldBe(plan.MembersToUpdate.Should().ContainSingle().Subject, new MemberWrite(User(1), "Nouveau", [], After, null));
    }

    [Fact]
    public void AReturnWithoutJoinDate_IsARejoinNotBeforeTheDeparture()
    {
        var plan = Compute(Sync([Member(1)]), Snapshot([Stored(1, joinedAt: Before, leftAt: LeftAt, lastLeftEventId: 77)]));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.Rejoined, Iso(Before), null, null, LeftAt, Now));
        plan.EventIdsToDelete.Should().BeEmpty();
        ShouldBe(plan.MembersToUpdate.Should().ContainSingle().Subject, new MemberWrite(User(1), null, [], null, null));
    }

    [Fact]
    public void AReturnWhoseOldJoinDateIsUnknown_IsARejoin()
    {
        var plan = Compute(Sync([Member(1, joinedAt: Before)]), Snapshot([Stored(1, leftAt: LeftAt, lastLeftEventId: 77)]));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.Rejoined, null, Iso(Before), Before, null, Now));
        plan.EventIdsToDelete.Should().BeEmpty();
    }

    // ── Unknown member ──

    [Theory]
    [InlineData(0)]
    [InlineData(72)]
    public void ANewcomerSinceTrackingStarted_JoinsAtTheirJoinDate(int hoursAfterFirstSync)
    {
        var joinedAt = FirstSync.AddHours(hoursAfterFirstSync);

        var plan = Compute(Sync([Member(1, joinedAt: joinedAt)]), Snapshot());

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.Joined, null, Iso(joinedAt), joinedAt, null, Now));
        plan.MembersToInsert.Should().ContainSingle().Which.JoinedAt.Should().Be(joinedAt);
    }

    [Fact]
    public void AMemberFromBeforeTracking_IsInsertedSilently()
    {
        var plan = Compute(Sync([Member(1, joinedAt: FirstSync.AddSeconds(-1))]), Snapshot());

        plan.Events.Should().BeEmpty();
        plan.MembersToInsert.Should().ContainSingle().Which.UserId.Should().Be(User(1));
    }

    [Fact]
    public void ANewcomerWithoutJoinDate_JoinsNotBeforeTheLastCompleteSync()
    {
        var plan = Compute(Sync([Member(1)]), Snapshot(lastCompleteSyncAt: LastSeen));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(1), DiscordEventTypes.Joined, null, null, null, LastSeen, Now));
        plan.MembersToInsert.Should().ContainSingle();
    }

    [Fact]
    public void ANewcomerWithoutJoinDate_BeforeAnyCompleteSync_IsInsertedSilently()
    {
        var plan = Compute(Sync([Member(1)]), Snapshot());

        plan.Events.Should().BeEmpty();
        plan.MembersToInsert.Should().ContainSingle();
    }

    // ── Departures ──

    [Fact]
    public void ACompleteSync_MarksAbsentActiveMembersAsLeft()
    {
        var stored = Enumerable.Range(1, 9).Select(n => Stored(n))
            .Append(Stored(10, joinedAt: Before))
            .Append(Stored(11, leftAt: LeftAt)); // already gone: untouched

        var plan = Compute(Sync(Enumerable.Range(1, 9).Select(n => Member(n))), Snapshot(stored));

        plan.Events.Should().Equal(new PlannedEvent(Guild, User(10), DiscordEventTypes.Left, Iso(Before), null, null, LastSeen, Now));
        ShouldBe(plan.MembersToUpdate.Should().ContainSingle().Subject, new MemberWrite(User(10), null, [], Before, Now));
        plan.IsComplete.Should().BeTrue();
        plan.MassDepartureDetected.Should().BeFalse();
    }

    [Fact]
    public void APartialSync_NeverDeparts()
    {
        var plan = Compute(Sync([Member(1)], complete: false), Snapshot(Enumerable.Range(1, 40).Select(n => Stored(n))));

        plan.Events.Should().BeEmpty();
        plan.MembersToUpdate.Should().BeEmpty();
        plan.MassDepartureDetected.Should().BeFalse();
        plan.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void AMassDeparture_IsSignalled_AndEveryDepartureIsRecorded()
    {
        var plan = Compute(Sync(Enumerable.Range(1, 29).Select(n => Member(n))),
            Snapshot(Enumerable.Range(1, 40).Select(n => Stored(n))));

        plan.MassDepartureDetected.Should().BeTrue();
        plan.IsComplete.Should().BeTrue();
        plan.Events.Should().HaveCount(11).And.OnlyContain(e => e.Type == DiscordEventTypes.Left);
        plan.MembersToUpdate.Should().HaveCount(11).And.OnlyContain(m => m.LeftAt == Now);
    }

    [Theory]
    [InlineData(40, 10)] // exactly 25 %: not more than the ratio
    [InlineData(20, 9)]  // 45 %, but fewer than 10
    public void DeparturesAtTheRatio_OrBelowTheMinimum_AreRecordedWithoutAMassSignal(int active, int gone)
    {
        var plan = Compute(Sync(Enumerable.Range(1, active - gone).Select(n => Member(n))),
            Snapshot(Enumerable.Range(1, active).Select(n => Stored(n))));

        plan.MassDepartureDetected.Should().BeFalse();
        plan.IsComplete.Should().BeTrue();
        plan.Events.Should().HaveCount(gone).And.OnlyContain(e => e.Type == DiscordEventTypes.Left);
    }



    // ── Idempotence, opt-outs, deleted roles ──

    [Fact]
    public void TheSamePayloadTwice_LogsNothingTheSecondTime()
    {
        var sync = Sync(
            [Member(1, nick: "A", roles: [RoleA, RoleB], joinedAt: Before, globalName: "G"), Member(2, joinedAt: FirstSync.AddDays(2)), Member(3)],
            [Role(RoleA, "Recrue"), Role(RoleB, "Officier", 10)]);

        var first = Compute(sync, Snapshot());
        var second = Compute(sync, SnapshotAfter(sync));

        first.Events.Should().ContainSingle().Which.Type.Should().Be(DiscordEventTypes.Joined);
        second.Events.Should().BeEmpty();
        second.MembersToInsert.Should().BeEmpty();
        second.MembersToUpdate.Should().BeEmpty();
        second.AccountsToInsert.Should().BeEmpty();
        second.AccountsToUpdate.Should().BeEmpty();
        second.RolesToInsert.Should().BeEmpty();
        second.RoleIdsToMarkDeleted.Should().BeEmpty();
        second.EventIdsToDelete.Should().BeEmpty();
        second.PresentUserIds.Should().Equal(User(1), User(2), User(3)); // only LastSeenAt moves
    }

    [Fact]
    public void OptedOutMembers_AreNeitherStoredNorDeparted()
    {
        // Member 2 was stored before the opt-out was recorded: the sync still lists them.
        var plan = Compute(Sync([Member(1), Member(2), Member(3)]), Snapshot([Stored(1), Stored(2)]), new HashSet<string> { User(2), User(3) });

        plan.OptedOutCount.Should().Be(2);
        plan.IsComplete.Should().BeTrue();
        plan.MembersToInsert.Should().BeEmpty();
        plan.MembersToUpdate.Should().BeEmpty();
        plan.Events.Should().BeEmpty();
        plan.PresentUserIds.Should().Equal(User(1));
    }

    [Fact]
    public void OptedOutStoredMember_IsIgnoredWhenAbsentFromThePayload()
    {
        var plan = Compute(Sync([Member(1)]), Snapshot([Stored(1), Stored(2)]), new HashSet<string> { User(2) });

        plan.IsComplete.Should().BeTrue();
        plan.MembersToUpdate.Should().BeEmpty();
        plan.Events.Should().BeEmpty();
        plan.PresentUserIds.Should().Equal(User(1));
        plan.OptedOutCount.Should().Be(0);
    }

    [Fact]
    public void OptedOutStoredMembers_AreExcludedFromDeparturesAndTheMassSignal()
    {
        var optedOut = Enumerable.Range(1, 25).Select(User).ToHashSet(StringComparer.Ordinal);
        var plan = Compute(Sync(Enumerable.Range(26, 5).Select(n => Member(n))),
            Snapshot(Enumerable.Range(1, 40).Select(n => Stored(n))), optedOut);

        plan.MassDepartureDetected.Should().BeTrue("ten departures among fifteen non-opted-out members exceed 25 percent");
        plan.IsComplete.Should().BeTrue();
        plan.MembersToUpdate.Should().HaveCount(10).And.OnlyContain(m => m.LeftAt != null);
        plan.Events.Should().HaveCount(10).And.OnlyContain(e => e.Type == DiscordEventTypes.Left);
    }

    [Fact]
    public void ARoleDeletedDuringTheCollection_LogsNoRolesChanged()
    {
        // The validator already dropped RoleB from the member (unknownRoleRefs).
        var snapshot = Snapshot([Stored(1, roles: [RoleA, RoleB])],
            roles: [new RoleSnapshot(RoleA, "Recrue", false), new RoleSnapshot(RoleB, "Officier", false)]);

        var plan = Compute(Sync([Member(1, roles: [RoleA])], [Role(RoleA, "Recrue")]), snapshot);

        plan.Events.Should().BeEmpty();
        plan.RoleIdsToMarkDeleted.Should().Equal(RoleB);
        plan.MembersToUpdate.Should().ContainSingle().Which.RoleIds.Should().Equal(RoleA); // replaced silently
    }

    [Fact]
    public void ARoleDeletedEarlier_StillOnAnUnseenMember_LogsNoRolesChanged()
    {
        var snapshot = Snapshot([Stored(1, roles: [RoleA, RoleB])],
            roles: [new RoleSnapshot(RoleA, "Recrue", false), new RoleSnapshot(RoleB, "Officier", true)]);

        var plan = Compute(Sync([Member(1, roles: [RoleA])], [Role(RoleA, "Recrue")]), snapshot);

        plan.Events.Should().BeEmpty();
        plan.RoleIdsToMarkDeleted.Should().BeEmpty();
        plan.MembersToUpdate.Should().ContainSingle().Which.RoleIds.Should().Equal(RoleA);
    }
}
