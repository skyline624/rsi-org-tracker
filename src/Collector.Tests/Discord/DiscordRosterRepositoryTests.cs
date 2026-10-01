using System.Globalization;
using System.Data.Common;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Extensions;
using Collector.Models;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// DiscordRosterRepository on a real tracker.db file, so that a second connection can hold
/// the write lock as the collector does: snapshot reads, the writes of a plan, bounded
/// transactions, an interrupted baseline, and SQLITE_BUSY.
/// </summary>
public sealed class DiscordRosterRepositoryTests : IAsyncLifetime
{
    private const string Guild = "100000000000000001";
    private const string OtherGuild = "100000000000000002";
    private const string RoleA = "300000000000000001";
    private const string RoleB = "300000000000000002";

    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddDays(1);
    private static readonly DateTime T2 = T0.AddDays(2);
    private static readonly DateTime Joined = new(2025, 3, 14, 20, 11, 5, DateTimeKind.Utc);

    private static readonly NormalizedRole Officer = new(RoleA, "Officier", 12, "#e67e22", true, false);
    private static readonly NormalizedRole Pings = new(RoleB, "Pings", 3, null, false, false);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sct-discord-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dataDir, "tracker.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        await using var provider = DataServices();
        await provider.EnsureDatabaseAsync(_dataDir);
    }

    private ServiceProvider DataServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCollectorDataServices(new ConfigurationBuilder().Build(), _dataDir);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A context of its own on the file, as each API request gets. Unpooled: a pooled handle
    /// would keep the busy_timeout pragma of the data services and make the busy tests wait 5 s.
    /// </summary>
    private TrackerDbContext NewDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False")
            .AddInterceptors(interceptors)
            .Options);

    private async Task SeedAsync(params object[] rows)
    {
        await using var db = NewDb();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static string User(int n) => (200000000000000000L + n).ToString(CultureInfo.InvariantCulture);

    private static NormalizedMember Member(int n, string? nick = null, string[]? roles = null, DateTime? joinedAt = null, string? username = null) =>
        new(User(n), username ?? $"user{n}", null, nick, (roles ?? []).Order(StringComparer.Ordinal).ToList(), joinedAt, false);

    private static NormalizedSync Sync(IEnumerable<NormalizedMember> members, IEnumerable<NormalizedRole>? roles = null, bool complete = true)
    {
        var list = members.ToList();
        return new NormalizedSync(Guild, "Ma Corpo", null, list.Count, DiscordSyncMethods.MemberSearch, complete, complete,
            list.Count, list.Count, "1.0.0", T0, TimeSpan.FromMinutes(1), (roles ?? [Officer, Pings]).ToList(), list, 0);
    }

    /// <summary>What the ingest service does: snapshot, diff, apply. Received one minute after collectedAt.</summary>
    private static async Task<(RosterPlan Plan, DiscordSyncResult Result)> IngestAsync(
        DiscordRosterRepository repository, NormalizedSync sync, DateTime collectedAt)
    {
        var ids = sync.Members.Select(m => m.UserId).ToList();
        var snapshot = await repository.LoadSnapshotAsync(Guild, ids);
        var optedOut = await repository.GetOptedOutAsync(ids);
        var plan = DiscordRosterDiff.Compute(Guild, sync, collectedAt, snapshot, optedOut);
        var result = await repository.ApplyAsync(new DiscordSyncWrite(sync, plan, collectedAt, collectedAt.AddMinutes(1), 7, "officer"));
        return (plan, result);
    }

    private async Task<(RosterPlan Plan, DiscordSyncResult Result)> IngestAsync(NormalizedSync sync, DateTime collectedAt)
    {
        await using var db = NewDb();
        return await IngestAsync(new DiscordRosterRepository(db), sync, collectedAt);
    }

    // ── Reads ──

    [Fact]
    public async Task IsGuildExcluded_ReadsTheGuildOptOuts()
    {
        await SeedAsync(new DiscordGuildOptOut { GuildId = Guild, CreatedAt = T0, ByUsername = "admin" });
        await using var db = NewDb();
        var repository = new DiscordRosterRepository(db);

        (await repository.IsGuildExcludedAsync(Guild)).Should().BeTrue();
        (await repository.IsGuildExcludedAsync(OtherGuild)).Should().BeFalse();
    }

    [Fact]
    public async Task LastSyncReceivedAt_IsTheLatestAcceptedSync()
    {
        await using (var db = NewDb())
        {
            (await new DiscordRosterRepository(db).GetLastSyncReceivedAtAsync(Guild)).Should().BeNull();
        }

        await IngestAsync(Sync([Member(1)]), T0);
        await IngestAsync(Sync([Member(1)]), T1);

        await using var check = NewDb();
        (await new DiscordRosterRepository(check).GetLastSyncReceivedAtAsync(Guild)).Should().Be(T1.AddMinutes(1));
    }

    [Fact]
    public async Task OptedOut_ReturnsTheOptedOutIdsAmongThoseAsked()
    {
        await SeedAsync(
            new DiscordOptOut { DiscordUserId = User(1), CreatedAt = T0, ByUsername = "admin" },
            new DiscordOptOut { DiscordUserId = User(3), CreatedAt = T0, ByUsername = "admin" });
        await using var db = NewDb();

        var optedOut = await new DiscordRosterRepository(db).GetOptedOutAsync([User(1), User(2)]);

        optedOut.Should().BeEquivalentTo([User(1)]);
    }

    [Fact]
    public async Task Snapshot_HoldsTheServer_AllItsRolesAndMembers_AndThePayloadsAccounts()
    {
        await SeedAsync(
            new DiscordGuild
            {
                GuildId = Guild, Name = "Ma Corpo", FirstSyncAt = T0, LastSyncAt = T1, LastCollectedAt = T1,
                LastCompleteSyncAt = T1, AllowMassDepartureOnce = true, CreatedAt = T0, UpdatedAt = T1,
            },
            new DiscordRole { GuildId = Guild, RoleId = RoleA, Name = "Officier", FirstSeenAt = T0, LastSeenAt = T1 },
            new DiscordRole { GuildId = Guild, RoleId = RoleB, Name = "Ancien", FirstSeenAt = T0, LastSeenAt = T0, DeletedAt = T1 },
            new DiscordRole { GuildId = OtherGuild, RoleId = "300000000000000009", Name = "Ailleurs", FirstSeenAt = T0, LastSeenAt = T0 },
            new DiscordMember
            {
                GuildId = Guild, DiscordUserId = User(1), Nick = "Pilote", RoleIdsJson = $"[\"{RoleA}\"]",
                JoinedAt = Joined, FirstSeenAt = T0, LastSeenAt = T1,
            },
            new DiscordMember { GuildId = Guild, DiscordUserId = User(2), FirstSeenAt = T0, LastSeenAt = T0, LeftAt = T1 },
            new DiscordMember { GuildId = OtherGuild, DiscordUserId = User(3), FirstSeenAt = T0, LastSeenAt = T0 },
            new DiscordAccount { DiscordUserId = User(1), Username = "user1", GlobalName = "Pilote", FirstSeenAt = T0, LastSeenAt = T1 },
            new DiscordAccount { DiscordUserId = User(2), Username = "user2", FirstSeenAt = T0, LastSeenAt = T0 },
            new DiscordAccount { DiscordUserId = User(3), Username = "user3", FirstSeenAt = T0, LastSeenAt = T0 });
        var firstLeft = new DiscordMemberEvent { GuildId = Guild, DiscordUserId = User(2), SyncId = 1, Type = DiscordEventTypes.Left, ObservedAt = T0 };
        var lastLeft = new DiscordMemberEvent { GuildId = Guild, DiscordUserId = User(2), SyncId = 2, Type = DiscordEventTypes.Left, ObservedAt = T1 };
        var elsewhere = new DiscordMemberEvent { GuildId = OtherGuild, DiscordUserId = User(2), SyncId = 3, Type = DiscordEventTypes.Left, ObservedAt = T2 };
        await SeedAsync(firstLeft, lastLeft, elsewhere);
        await using var db = NewDb();

        var snapshot = await new DiscordRosterRepository(db).LoadSnapshotAsync(Guild, [User(1), User(2), User(4)]);

        snapshot.Guild.Should().Be(new GuildSnapshot(T0, T1));
        snapshot.Roles.Values.Should().BeEquivalentTo([new RoleSnapshot(RoleA, "Officier", false), new RoleSnapshot(RoleB, "Ancien", true)]);
        snapshot.Members.Keys.Should().BeEquivalentTo([User(1), User(2)]);
        snapshot.Members[User(1)].Should().BeEquivalentTo(
            new MemberSnapshot(User(1), "Pilote", [RoleA], Joined, T1, null, null), o => o.ComparingByMembers<MemberSnapshot>());
        snapshot.Members[User(2)].LastLeftEventId.Should().Be(lastLeft.Id);
        // LastSeenAt travels with the account: the diff never applies names older than the stored ones.
        snapshot.Accounts.Values.Should().BeEquivalentTo([
            new AccountSnapshot(User(1), "user1", "Pilote", IsBot: false, LastSeenAt: T1),
            new AccountSnapshot(User(2), "user2", null, IsBot: false, LastSeenAt: T0)]);
    }

    [Fact]
    public async Task AnUntrackedServer_HasAnEmptySnapshot()
    {
        await using var db = NewDb();

        var snapshot = await new DiscordRosterRepository(db).LoadSnapshotAsync(Guild, [User(1)]);

        snapshot.Guild.Should().BeNull();
        snapshot.Roles.Should().BeEmpty();
        snapshot.Members.Should().BeEmpty();
        snapshot.Accounts.Should().BeEmpty();
    }

    // ── Writes ──

    [Fact]
    public async Task ABaseline_WritesTheServer_ItsRoles_AccountsAndMembers_AndTheSyncRow()
    {
        var (_, result) = await IngestAsync(Sync([Member(1, nick: "Pilote", roles: [RoleA], joinedAt: Joined), Member(2)]), T0);

        await using var db = NewDb();
        (await db.DiscordGuilds.SingleAsync()).Should().BeEquivalentTo(new
        {
            GuildId = Guild, Name = "Ma Corpo", MemberCount = (int?)2, FirstSyncAt = T0, LastCollectedAt = T0,
            LastSyncAt = T0.AddMinutes(1), LastCompleteSyncAt = (DateTime?)T0, AllowMassDepartureOnce = false,
        });
        (await db.DiscordRoles.OrderBy(r => r.RoleId).ToListAsync()).Should().BeEquivalentTo(new[]
        {
            new { RoleId = RoleA, Name = "Officier", Position = 12, Color = (string?)"#e67e22", IsRank = true, RankOrder = (int?)12, FirstSeenAt = T0, DeletedAt = (DateTime?)null },
            new { RoleId = RoleB, Name = "Pings", Position = 3, Color = (string?)null, IsRank = false, RankOrder = (int?)null, FirstSeenAt = T0, DeletedAt = (DateTime?)null },
        });
        (await db.DiscordMembers.SingleAsync(m => m.DiscordUserId == User(1))).Should().BeEquivalentTo(new
        {
            GuildId = Guild, Nick = "Pilote", RoleIdsJson = $"[\"{RoleA}\"]", JoinedAt = (DateTime?)Joined,
            FirstSeenAt = T0, LastSeenAt = T0, LeftAt = (DateTime?)null,
        });
        (await db.DiscordAccounts.SingleAsync(a => a.DiscordUserId == User(1))).Should().BeEquivalentTo(new
        {
            Username = "user1", GlobalName = (string?)null, IsBot = false, FirstSeenAt = T0, LastSeenAt = T0,
        });
        (await db.DiscordSyncs.SingleAsync()).Should().BeEquivalentTo(new
        {
            Id = result.SyncId, GuildId = Guild, SubmittedByApiUserId = 7L, SubmittedByUsername = "officer",
            ReceivedAt = T0.AddMinutes(1), CollectedAt = T0, DeclaredCollectedAt = T0, Method = DiscordSyncMethods.MemberSearch,
            DeclaredComplete = true, IsComplete = true, IsBaseline = true, MassDepartureDetected = false,
            ExpectedCount = (int?)2, CollectedCount = 2, OptedOutCount = 0, UnknownRoleRefCount = 0, EventCount = 0,
            PluginVersion = "1.0.0",
        });
        (await db.DiscordMemberEvents.CountAsync()).Should().Be(0);
        result.OrgSid.Should().BeNull();
    }

    [Fact]
    public async Task AnUpdate_WritesItsEventsWithTheSyncId_UpdatesTheRows_AndMovesLastSeenAt()
    {
        await IngestAsync(Sync([Member(1, nick: "A", roles: [RoleA]), Member(2), Member(3)]), T0);
        await using (var map = NewDb())
        {
            await map.DiscordGuilds.ExecuteUpdateAsync(s => s.SetProperty(g => g.OrgSid, "CORP"));
        }

        // Member 1 renames, member 2 is gone, account 3 renames, RoleA is renamed, RoleB is deleted.
        var captain = Officer with { Name = "Capitaine" };
        var (_, result) = await IngestAsync(Sync([Member(1, nick: "B", roles: [RoleA]), Member(3, username: "renamed")], [captain]), T1);

        await using var db = NewDb();
        var events = await db.DiscordMemberEvents.OrderBy(e => e.Id).ToListAsync();
        events.Select(e => (e.Type, e.DiscordUserId, e.SyncId)).Should().Equal(
            (DiscordEventTypes.UsernameChanged, User(3), result.SyncId),
            (DiscordEventTypes.NickChanged, User(1), result.SyncId),
            (DiscordEventTypes.Left, User(2), result.SyncId));
        (await db.DiscordMembers.SingleAsync(m => m.DiscordUserId == User(1))).Should().BeEquivalentTo(new { Nick = "B", LastSeenAt = T1 });
        (await db.DiscordMembers.SingleAsync(m => m.DiscordUserId == User(2))).Should().BeEquivalentTo(new { LeftAt = (DateTime?)T1, LastSeenAt = T0 });
        (await db.DiscordMembers.SingleAsync(m => m.DiscordUserId == User(3))).LastSeenAt.Should().Be(T1);
        (await db.DiscordAccounts.SingleAsync(a => a.DiscordUserId == User(3))).Should().BeEquivalentTo(new { Username = "renamed", LastSeenAt = T1 });
        (await db.DiscordAccounts.SingleAsync(a => a.DiscordUserId == User(2))).LastSeenAt.Should().Be(T0);
        (await db.DiscordRoles.SingleAsync(r => r.RoleId == RoleA)).Should().BeEquivalentTo(new
        {
            Name = "Capitaine", IsRank = true, RankOrder = (int?)12, LastSeenAt = T1, DeletedAt = (DateTime?)null,
        });
        (await db.DiscordRoles.SingleAsync(r => r.RoleId == RoleB)).DeletedAt.Should().Be(T1);
        (await db.DiscordGuilds.SingleAsync()).Should().BeEquivalentTo(new
        {
            FirstSyncAt = T0, LastCollectedAt = T1, LastSyncAt = T1.AddMinutes(1), LastCompleteSyncAt = (DateTime?)T1,
        });
        (await db.DiscordSyncs.SingleAsync(s => s.Id == result.SyncId)).Should().BeEquivalentTo(new { IsBaseline = false, EventCount = 3 });
        result.OrgSid.Should().Be("CORP");
    }

    [Fact]
    public async Task AFalseDepartureCorrection_DeletesItsLeftEvent()
    {
        await IngestAsync(Sync([Member(1, joinedAt: Joined), Member(2, joinedAt: Joined)]), T0);
        await IngestAsync(Sync([Member(1, joinedAt: Joined)]), T1); // member 2 missed: marked as gone

        var (plan, _) = await IngestAsync(Sync([Member(1, joinedAt: Joined), Member(2, joinedAt: Joined)]), T2);

        plan.EventIdsToDelete.Should().ContainSingle();
        await using var db = NewDb();
        (await db.DiscordMemberEvents.CountAsync(e => e.DiscordUserId == User(2))).Should().Be(0);
        (await db.DiscordMembers.SingleAsync(m => m.DiscordUserId == User(2))).LeftAt.Should().BeNull();
    }

    [Fact]
    public async Task AMassDeparture_UpdatesTheCompleteSync_AndIsLoggedWithoutBlocking()
    {
        await IngestAsync(Sync(Enumerable.Range(1, 40).Select(n => Member(n))), T0);
        await IngestAsync(Sync(Enumerable.Range(1, 29).Select(n => Member(n))), T1);

        await using var db = NewDb();
        (await db.DiscordGuilds.SingleAsync()).LastCompleteSyncAt.Should().Be(T1);
        (await db.DiscordSyncs.OrderBy(s => s.Id).LastAsync()).Should().BeEquivalentTo(new
        {
            DeclaredComplete = true, IsComplete = true, MassDepartureDetected = true, EventCount = 11,
        });
        (await db.DiscordMembers.CountAsync(m => m.LeftAt != null)).Should().Be(11);
    }

    [Fact]
    public async Task ALegacyAllowanceFlag_DoesNotChangeAutomaticDepartureRecording()
    {
        await IngestAsync(Sync(Enumerable.Range(1, 40).Select(n => Member(n))), T0);
        await using (var admin = NewDb())
        {
            await admin.DiscordGuilds.ExecuteUpdateAsync(s => s.SetProperty(g => g.AllowMassDepartureOnce, true));
        }

        await IngestAsync(Sync(Enumerable.Range(1, 29).Select(n => Member(n))), T1);

        await using var db = NewDb();
        (await db.DiscordGuilds.SingleAsync()).Should().BeEquivalentTo(new { AllowMassDepartureOnce = true, LastCompleteSyncAt = (DateTime?)T1 });
        (await db.DiscordMembers.CountAsync(m => m.LeftAt != null)).Should().Be(11);
    }

    [Fact]
    public async Task LastSeenAt_NeverMovesBack()
    {
        await IngestAsync(Sync([Member(1)]), T1);

        await IngestAsync(Sync([Member(1)]), T0);

        await using var db = NewDb();
        (await db.DiscordMembers.SingleAsync()).LastSeenAt.Should().Be(T1);
        (await db.DiscordAccounts.SingleAsync()).LastSeenAt.Should().Be(T1);
    }

    // ── Bounded transactions ──

    [Fact]
    public async Task NoTransaction_WritesMoreThan5000Rows_ForA12000MemberBaseline()
    {
        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter))
        {
            await IngestAsync(new DiscordRosterRepository(db), Sync(Enumerable.Range(1, 12_000).Select(n => Member(n, joinedAt: Joined))), T0);
        }

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordRosterRepository.MaxRowsPerTransaction);
        counter.Rows.Sum().Should().BeGreaterThanOrEqualTo(24_000, "every member and account row went through a counted transaction");
        await using var check = NewDb();
        (await check.DiscordMembers.CountAsync()).Should().Be(12_000);
        (await check.DiscordAccounts.CountAsync()).Should().Be(12_000);
    }

    [Fact]
    public async Task DeletingARoleThat6000MembersHold_RewritesThemInBoundedTransactions_AndLogsNothing()
    {
        await IngestAsync(Sync(Enumerable.Range(1, 6_000).Select(n => Member(n, roles: [RoleB]))), T0);
        var counter = new SqliteRowsPerTransaction();

        // RoleB is gone: each holder's RoleIdsJson loses it, with no roles_changed (spec § 9.3).
        await using (var db = NewDb(counter))
        {
            await IngestAsync(new DiscordRosterRepository(db), Sync(Enumerable.Range(1, 6_000).Select(n => Member(n)), [Officer]), T1);
        }

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordRosterRepository.MaxRowsPerTransaction);
        await using var check = NewDb();
        (await check.DiscordMembers.CountAsync(m => m.RoleIdsJson == "[]")).Should().Be(6_000);
        (await check.DiscordMemberEvents.CountAsync()).Should().Be(0);
        (await check.DiscordRoles.SingleAsync(r => r.RoleId == RoleB)).DeletedAt.Should().Be(T1);
    }

    /// <summary>The process dies while writing the second batch of new members.</summary>
    private sealed class FailSecondMemberBatch : SaveChangesInterceptor
    {
        private int _memberBatches;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var addsMembers = eventData.Context!.ChangeTracker.Entries<DiscordMember>().Any(e => e.State == EntityState.Added);
            if (addsMembers && ++_memberBatches == 2) throw new InvalidOperationException("killed during the second member batch");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task AnInterruptedBaseline_IsCompletedByTheNextSync_WithoutEvents()
    {
        // Half the members have no join date: those follow the "unknown member, no complete sync yet" rule.
        var sync = Sync(Enumerable.Range(1, 6_000).Select(n => Member(n, joinedAt: n % 2 == 0 ? Joined : null)));
        await using (var db = NewDb(new FailSecondMemberBatch()))
        {
            var act = () => IngestAsync(new DiscordRosterRepository(db), sync, T0);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("killed*");
        }
        await using (var interrupted = NewDb())
        {
            (await interrupted.DiscordGuilds.SingleAsync()).LastCompleteSyncAt.Should().BeNull();
            (await interrupted.DiscordMembers.CountAsync()).Should().Be(DiscordRosterRepository.MaxRowsPerTransaction);
            (await interrupted.DiscordSyncs.CountAsync(s => s.EventCount >= 0)).Should().Be(0);
            (await interrupted.DiscordSyncs.SingleAsync()).EventCount.Should().Be(-1);
        }

        var (plan, _) = await IngestAsync(sync, T1);

        plan.IsBaseline.Should().BeTrue("an interrupted baseline must never turn its remaining rows into arrivals");
        plan.Events.Should().BeEmpty();
        await using var check = NewDb();
        (await check.DiscordMembers.CountAsync(m => m.LeftAt == null)).Should().Be(6_000);
        (await check.DiscordAccounts.CountAsync()).Should().Be(6_000);
        (await check.DiscordMemberEvents.CountAsync()).Should().Be(0);
        (await check.DiscordSyncs.CountAsync()).Should().Be(2);
        (await check.DiscordSyncs.CountAsync(s => s.IsComplete)).Should().Be(1);
        (await check.DiscordGuilds.SingleAsync()).Should().BeEquivalentTo(new { FirstSyncAt = T0, LastCompleteSyncAt = (DateTime?)T1 });
    }

    private sealed class FailFirstMemberBatch : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<DiscordMember>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("interrupted first member batch");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ErasingAnInterruptedBaseline_RemovesAccountsCreatedBeforeItsFirstMemberBatch()
    {
        await using (var db = NewDb(new FailFirstMemberBatch()))
        {
            var act = () => IngestAsync(new DiscordRosterRepository(db), Sync([Member(1)]), T0);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var interrupted = NewDb())
        {
            (await interrupted.DiscordAccounts.CountAsync()).Should().Be(1);
            (await interrupted.DiscordMembers.CountAsync()).Should().Be(0);
        }

        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter))
            (await new DiscordErasureRepository(db).EraseGuildAsync(Guild, false, 9, "admin")).Should().BeTrue();

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordRosterRepository.MaxRowsPerTransaction);
        await using var check = NewDb();
        (await check.DiscordAccounts.CountAsync()).Should().Be(0);
        (await check.DiscordGuilds.CountAsync()).Should().Be(0);
        (await check.DiscordSyncs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SixThousandVisibleChanges_KeepEventsAndStateInsideTheRowBudget()
    {
        await IngestAsync(Sync(Enumerable.Range(1, 6_000).Select(n => Member(n, "old", [RoleB], Joined))), T0);
        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter))
        {
            await IngestAsync(new DiscordRosterRepository(db), Sync(Enumerable.Range(1, 6_000)
                .Select(n => Member(n, "new", [RoleA], Joined, $"renamed{n}"))), T1);
        }

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordRosterRepository.MaxRowsPerTransaction);
        await using var check = NewDb();
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.NickChanged)).Should().Be(6_000);
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.RolesChanged)).Should().Be(6_000);
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.UsernameChanged)).Should().Be(6_000);
        (await check.DiscordMembers.CountAsync(m => m.Nick == "new")).Should().Be(6_000);
        (await check.DiscordSyncs.OrderByDescending(s => s.Id).FirstAsync()).EventCount.Should().Be(18_000);
    }

    [Fact]
    public async Task InterruptedNonBaseline_PreservesArrivalsAndAuthor_AndResumesRoleChanges()
    {
        await IngestAsync(Sync([Member(1, "before", [RoleB], Joined)]), T0);
        var sync = Sync(Enumerable.Range(2, 6_000).Select(n => Member(n, joinedAt: T1))
            .Append(Member(1, "after", [RoleA], Joined)));
        await using (var db = NewDb(new FailSecondMemberBatch()))
        {
            var fail = () => IngestAsync(new DiscordRosterRepository(db), sync, T1);
            await fail.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var interrupted = NewDb())
        {
            var pending = await interrupted.DiscordSyncs.SingleAsync(s => s.EventCount < 0);
            pending.IsComplete.Should().BeFalse();
            pending.SubmittedByUsername.Should().Be("officer");
            (await interrupted.DiscordMemberEvents.CountAsync(e => e.SyncId == pending.Id)).Should().Be(2_500);
            (await new DiscordRosterRepository(interrupted).GetLastSyncReceivedAtAsync(Guild)).Should().Be(T1.AddMinutes(1));
            (await interrupted.DiscordGuilds.SingleAsync()).LastCompleteSyncAt.Should().Be(T0);
        }

        await using (var resume = NewDb())
        {
            var repository = new DiscordRosterRepository(resume);
            var ids = sync.Members.Select(m => m.UserId).ToList();
            var snapshot = await repository.LoadSnapshotAsync(Guild, ids);
            var plan = DiscordRosterDiff.Compute(Guild, sync, T2, snapshot, new HashSet<string>());
            await repository.ApplyAsync(new DiscordSyncWrite(sync, plan, T2, T2.AddMinutes(1), 9, "rescuer"));
        }
        await using var check = NewDb();
        var journals = await check.DiscordSyncs.OrderBy(s => s.Id).ToListAsync();
        journals.Should().HaveCount(3);
        journals[1].Should().BeEquivalentTo(new
        {
            IsComplete = false, SubmittedByApiUserId = 7L, SubmittedByUsername = "officer",
            ReceivedAt = T1.AddMinutes(1), CollectedAt = T1, EventCount = 2_500,
        });
        journals[2].SubmittedByUsername.Should().Be("rescuer");
        journals[2].IsComplete.Should().BeTrue();
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.Joined)).Should().Be(6_000);
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.RolesChanged)).Should().Be(1);
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.NickChanged)).Should().Be(1);
        (await check.DiscordMembers.CountAsync()).Should().Be(6_001);
        (await check.DiscordGuilds.SingleAsync()).FirstSyncAt.Should().Be(T0);
        (await check.DiscordSyncs.AnyAsync(s => s.EventCount < 0)).Should().BeFalse();
    }

    [Fact]
    public async Task SixThousandAutomaticDepartures_StayBounded_AndAreSignalled()
    {
        await IngestAsync(Sync(Enumerable.Range(1, 6_500).Select(n => Member(n, joinedAt: Joined))), T0);
        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter))
            await IngestAsync(new DiscordRosterRepository(db), Sync(Enumerable.Range(1, 500).Select(n => Member(n, joinedAt: Joined))), T1);

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordRosterRepository.MaxRowsPerTransaction);
        await using var check = NewDb();
        (await check.DiscordMembers.CountAsync(m => m.LeftAt != null)).Should().Be(6_000);
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.Left)).Should().Be(6_000);
        (await check.DiscordSyncs.OrderByDescending(s => s.Id).FirstAsync()).MassDepartureDetected.Should().BeTrue();
        (await check.DiscordSyncs.OrderByDescending(s => s.Id).FirstAsync()).IsComplete.Should().BeTrue();
    }

    private sealed class FailSecondDepartureBatch : SaveChangesInterceptor
    {
        private int _batches;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var departures = eventData.Context!.ChangeTracker.Entries<DiscordMember>()
                .Any(e => e.State == EntityState.Modified && e.Entity.LeftAt != null);
            if (departures && ++_batches == 2) throw new InvalidOperationException("interrupted departure batch");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task InterruptedMassDepartures_ResumeLegacyPendingReceipts_AndRecordRemainingDepartures()
    {
        await IngestAsync(Sync(Enumerable.Range(1, 4_000).Select(n => Member(n, joinedAt: Joined))), T0);
        var sync = Sync(Enumerable.Range(1, 1_490).Select(n => Member(n, joinedAt: Joined)));
        await using (var db = NewDb(new FailSecondDepartureBatch()))
        {
            var act = () => IngestAsync(new DiscordRosterRepository(db), sync, T1);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var interrupted = NewDb())
        {
            (await interrupted.DiscordMembers.CountAsync(m => m.LeftAt != null)).Should().Be(2_500);
            var pending = await interrupted.DiscordSyncs.SingleAsync(s => s.EventCount < 0);
            pending.EventCount.Should().Be(-1);
            pending.EventCount = -2; // A receipt from the old allowance implementation must still resume.
            await interrupted.SaveChangesAsync();
        }

        var (plan, _) = await IngestAsync(sync, T2);

        plan.MassDepartureDetected.Should().BeFalse("only ten of 1,500 active members remain absent");
        await using var check = NewDb();
        (await check.DiscordMembers.CountAsync(m => m.LeftAt != null)).Should().Be(2_510);
        (await check.DiscordMemberEvents.CountAsync(e => e.Type == DiscordEventTypes.Left)).Should().Be(2_510);
        (await check.DiscordSyncs.AnyAsync(s => s.EventCount < 0)).Should().BeFalse();
        (await check.DiscordSyncs.OrderBy(s => s.Id).Skip(1).FirstAsync()).Should().BeEquivalentTo(new
        {
            IsComplete = false, EventCount = 2_500, ReceivedAt = T1.AddMinutes(1), CollectedAt = T1,
        });
    }

    private sealed class FailFreshnessUpdate : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("discord_members", StringComparison.Ordinal))
                throw new InvalidOperationException("interrupted freshness update");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task FailedFreshnessBatch_DoesNotPublishACompleteSync()
    {
        var sync = Sync([Member(1)]);
        await IngestAsync(sync, T0);
        await using (var db = NewDb(new FailFreshnessUpdate()))
        {
            var act = () => IngestAsync(new DiscordRosterRepository(db), sync, T1);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var interrupted = NewDb())
        {
            (await interrupted.DiscordSyncs.SingleAsync(s => s.EventCount < 0)).IsComplete.Should().BeFalse();
            (await interrupted.DiscordGuilds.SingleAsync()).LastCompleteSyncAt.Should().Be(T0);
            (await interrupted.DiscordMembers.SingleAsync()).LastSeenAt.Should().Be(T0);
        }
        await IngestAsync(sync, T2);
        await using var check = NewDb();
        (await check.DiscordMembers.SingleAsync()).LastSeenAt.Should().Be(T2);
        (await check.DiscordGuilds.SingleAsync()).LastCompleteSyncAt.Should().Be(T2);
    }

    [Fact]
    public async Task BotStatusChange_UpdatesTheAccountWithoutANameEvent()
    {
        await IngestAsync(Sync([Member(1)]), T0);

        await IngestAsync(Sync([Member(1) with { IsBot = true }]), T1);

        await using var check = NewDb();
        (await check.DiscordAccounts.SingleAsync()).IsBot.Should().BeTrue();
        (await check.DiscordMemberEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OlderCollectionFromAnotherGuild_KeepsTheNewestGlobalIdentity_AndStillAddsMembership()
    {
        var old = Member(1, "guild-A", username: "old-user") with { GlobalName = "Old display" };
        await IngestAsync(Sync([old]), T0);
        await IngestAsync(Sync([old with { Username = "new-user", GlobalName = "New display", IsBot = true }]), T2);
        var delayed = Sync([old with { Nick = "guild-B" }]) with { GuildId = OtherGuild, GuildName = "Other guild" };
        await using (var db = NewDb())
        {
            var repository = new DiscordRosterRepository(db);
            var snapshot = await repository.LoadSnapshotAsync(OtherGuild, [User(1)]);
            snapshot.Accounts[User(1)].LastSeenAt.Should().Be(T2);
            var plan = DiscordRosterDiff.Compute(OtherGuild, delayed, T1, snapshot, new HashSet<string>());
            plan.AccountsToUpdate.Should().BeEmpty();
            plan.Events.Should().BeEmpty();
            await repository.ApplyAsync(new DiscordSyncWrite(delayed, plan, T1, T2.AddHours(1), 9, "delayed-guild"));
        }

        await using var check = NewDb();
        (await check.DiscordAccounts.SingleAsync()).Should().BeEquivalentTo(new
        {
            Username = "new-user", GlobalName = "New display", IsBot = true, LastSeenAt = T2,
        });
        (await check.DiscordMembers.SingleAsync(m => m.GuildId == OtherGuild)).Should().BeEquivalentTo(new
        {
            Nick = "guild-B", FirstSeenAt = T1, LastSeenAt = T1,
        });
        (await check.DiscordMemberEvents.CountAsync()).Should().Be(2, "only the newer observation records real identity changes");
        (await check.DiscordSyncs.SingleAsync(s => s.GuildId == OtherGuild)).EventCount.Should().Be(0);
    }

    // ── SQLITE_BUSY ──

    /// <summary>Another writer (the collector) holding tracker.db's write lock.</summary>
    private sealed class WriteLock : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private bool _held = true;

        private WriteLock(SqliteConnection connection) => _connection = connection;

        public static async Task<WriteLock> TakeAsync(string path)
        {
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
            return new WriteLock(connection);
        }

        public async Task ReleaseAsync()
        {
            if (!_held) return;
            _held = false;
            await using var rollback = _connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task ABusyTransaction_IsRetriedOnceAfterTwoSeconds()
    {
        await using var writeLock = await WriteLock.TakeAsync(DbPath);
        var waits = new List<TimeSpan>();
        await using var db = NewDb();
        var repository = new DiscordRosterRepository(db)
        {
            BusyTimeout = TimeSpan.FromSeconds(1),
            // The collector commits while we wait: the retry finds the lock free.
            Delay = async (wait, _) => { waits.Add(wait); await writeLock.ReleaseAsync(); },
        };

        var (_, result) = await IngestAsync(repository, Sync([Member(1)]), T0);

        waits.Should().Equal(TimeSpan.FromSeconds(2));
        result.SyncId.Should().BePositive();
    }

    [Fact]
    public async Task ADatabaseStillBusyAfterTheRetry_ThrowsDiscordStoreBusy_AndALaterSyncSucceeds()
    {
        var writeLock = await WriteLock.TakeAsync(DbPath);
        var waits = new List<TimeSpan>();
        await using (var db = NewDb())
        {
            var repository = new DiscordRosterRepository(db)
            {
                BusyTimeout = TimeSpan.FromSeconds(1),
                Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
            };

            var act = () => IngestAsync(repository, Sync([Member(1)]), T0);

            (await act.Should().ThrowAsync<DiscordStoreBusyException>()).WithInnerException<SqliteException>();
        }
        waits.Should().Equal(TimeSpan.FromSeconds(2));
        await writeLock.DisposeAsync();

        var (_, result) = await IngestAsync(Sync([Member(1)]), T0);

        result.SyncId.Should().BePositive();
    }

    [Fact]
    public void TheRepository_ComesWithTheDataServices()
    {
        using var provider = DataServices();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDiscordRosterRepository>().Should().BeOfType<DiscordRosterRepository>();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}
