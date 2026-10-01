using System.Data.Common;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// Retention purges (spec § 13.3), one batch per call: sync log rows received before the
/// cutoff, and unlinked accounts whose every member row is older than it (or that have no
/// member row and were last seen before it), with their members, events and rejections.
/// </summary>
public sealed class DiscordRetentionRepositoryTests : IAsyncLifetime
{
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Cutoff.AddDays(-10);
    private static readonly DateTime Recent = Cutoff.AddDays(10);
    private const string G1 = "200000000000000001";
    private const string G2 = "200000000000000002";
    private const string A = "100000000000000001";
    private const string B = "100000000000000002";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteRowsPerTransaction _transactions = new();
    private readonly FailSecondEventDelete _failure = new();
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection)
            .AddInterceptors(_transactions, _failure).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    private DiscordRetentionRepository Repo() => new(_db);

    [Fact]
    public async Task AnAccountWithYearsOfHistory_IsPurgedInBoundedTransactions()
    {
        await SeedLargeHistoryAsync();
        _transactions.Rows.Clear();

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(1);

        (await RowsOfAsync(A)).Should().Be((0, 0, 0, 0));
        _transactions.Rows.Should().OnlyContain(rows => rows <= DiscordRetentionRepository.MaxRowsPerTransaction);
        _transactions.Rows.Count(rows => rows == 5000).Should().Be(2);
    }

    [Fact]
    public async Task InterruptedHistoryPurge_KeepsTheAccountUntilTheNextPassFinishes()
    {
        await SeedLargeHistoryAsync();
        _transactions.Rows.Clear();
        _failure.Armed = true;

        var purge = () => Repo().PurgeDepartedAccountsAsync(Cutoff, 500);
        await purge.Should().ThrowAsync<InvalidOperationException>().WithMessage("Interrupted history purge.");

        (await RowsOfAsync(A)).Should().Be((1, 1, 7000, 1));
        _failure.Armed = false;
        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(1);
        (await RowsOfAsync(A)).Should().Be((0, 0, 0, 0));
        _transactions.Rows.Should().OnlyContain(rows => rows <= DiscordRetentionRepository.MaxRowsPerTransaction);
    }

    [Fact]
    public async Task PendingReceipts_AreKeptForRecovery()
    {
        await SeedSyncsAsync(Old, Old);
        var pending = await _db.DiscordSyncs.OrderBy(s => s.Id).FirstAsync();
        pending.EventCount = -1;
        await _db.SaveChangesAsync();

        (await Repo().PurgeSyncLogsAsync(Cutoff, 500)).Should().Be(1);
        (await _db.DiscordSyncs.AsNoTracking().SingleAsync()).EventCount.Should().Be(-1);
    }

    [Fact]
    public async Task ARecentAccountObservation_ProtectsOldMembershipsDuringInterruptedIngestion()
    {
        await SeedAccountAsync(A, Recent);
        await SeedMemberAsync(G1, A, Old, Old);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);
        (await RowsOfAsync(A)).Should().Be((1, 1, 0, 0));
    }

    [Fact]
    public async Task SyncLogs_ReceivedBeforeTheCutoff_AreDeleted_NewerOnesKept()
    {
        await SeedSyncsAsync(Old, Cutoff.AddSeconds(-1), Cutoff, Recent);

        var deleted = await Repo().PurgeSyncLogsAsync(Cutoff, 500);

        deleted.Should().Be(2);
        (await _db.DiscordSyncs.AsNoTracking().Select(s => s.ReceivedAt).ToListAsync())
            .Should().BeEquivalentTo(new[] { Cutoff, Recent });
    }

    [Fact]
    public async Task SyncLogs_AreDeletedOneBatchAtATime()
    {
        await SeedSyncsAsync(Old, Old, Old, Old, Old);
        var repo = Repo();

        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(1);
        (await repo.PurgeSyncLogsAsync(Cutoff, 2)).Should().Be(0);
    }

    [Fact]
    public async Task AnUnlinkedAccount_GoneFromEveryGuild_IsPurged_WithItsMembersEventsAndRejections()
    {
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, lastSeen: Old.AddDays(-30), leftAt: Old);
        await SeedMemberAsync(G2, A, lastSeen: Old, leftAt: null);
        await SeedEventAsync(G1, A);
        await SeedEventAsync(null, A);
        await SeedRejectionAsync(A);
        // A bystander still present keeps every row.
        await SeedAccountAsync(B, Recent);
        await SeedMemberAsync(G1, B, lastSeen: Recent, leftAt: null);
        await SeedEventAsync(G1, B);
        await SeedRejectionAsync(B);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(1);

        (await RowsOfAsync(A)).Should().Be((0, 0, 0, 0));
        (await RowsOfAsync(B)).Should().Be((1, 1, 1, 1));
    }

    [Fact]
    public async Task ALinkedAccount_IsKept()
    {
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, lastSeen: Old, leftAt: Old);
        await SeedEventAsync(G1, A);
        await SeedLinkAsync(A);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);

        (await RowsOfAsync(A)).Should().Be((1, 1, 1, 0));
    }

    [Fact]
    public async Task AnAccountWithoutMemberRows_IsPurgedOnlyOnceItsLastSightingIsOld()
    {
        await SeedAccountAsync(A, Old);
        await SeedAccountAsync(B, Recent);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(1);

        (await RowsOfAsync(A)).Accounts.Should().Be(0);
        (await RowsOfAsync(B)).Accounts.Should().Be(1);
    }

    [Fact]
    public async Task AnAccountStillActiveInOneGuild_IsKept()
    {
        await SeedAccountAsync(A, Recent);
        await SeedMemberAsync(G1, A, lastSeen: Old, leftAt: Old);
        await SeedMemberAsync(G2, A, lastSeen: Recent, leftAt: null);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);

        (await RowsOfAsync(A)).Should().Be((1, 2, 0, 0));
    }

    [Fact]
    public async Task ARecentDeparture_KeepsTheAccount_EvenWhenItsLastSightingIsOld()
    {
        // coalesce(LeftAt, LastSeenAt): the departure date counts, not the last sighting.
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, lastSeen: Old, leftAt: Recent);

        (await Repo().PurgeDepartedAccountsAsync(Cutoff, 500)).Should().Be(0);

        (await RowsOfAsync(A)).Accounts.Should().Be(1);
    }

    [Fact]
    public async Task DepartedAccounts_ArePurgedOneBatchAtATime()
    {
        for (var i = 1; i <= 5; i++) await SeedAccountAsync($"10000000000000010{i}", Old);
        var repo = Repo();

        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(2);
        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(1);
        (await repo.PurgeDepartedAccountsAsync(Cutoff, 2)).Should().Be(0);
    }

    private async Task<(int Accounts, int Members, int Events, int Rejections)> RowsOfAsync(string userId) => (
        await _db.DiscordAccounts.CountAsync(a => a.DiscordUserId == userId),
        await _db.DiscordMembers.CountAsync(m => m.DiscordUserId == userId),
        await _db.DiscordMemberEvents.CountAsync(e => e.DiscordUserId == userId),
        await _db.DiscordLinkRejections.CountAsync(r => r.DiscordUserId == userId));

    private async Task SeedSyncsAsync(params DateTime[] receivedAt)
    {
        foreach (var at in receivedAt)
            _db.DiscordSyncs.Add(new DiscordSync
            {
                GuildId = G1, SubmittedByApiUserId = 1, SubmittedByUsername = "sender",
                ReceivedAt = at, CollectedAt = at, DeclaredCollectedAt = at,
                Method = DiscordSyncMethods.MemberSearch, PluginVersion = "1.0.0",
            });
        await SaveAsync();
    }

    private async Task SeedLargeHistoryAsync()
    {
        await SeedAccountAsync(A, Old);
        await SeedMemberAsync(G1, A, Old, Old);
        await SeedRejectionAsync(A);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 12000)
            INSERT INTO discord_member_events (DiscordUserId, SyncId, Type, ObservedAt)
            SELECT {A}, 0, {DiscordEventTypes.UsernameChanged}, {Old} FROM n;
            """);
    }

    private sealed class FailSecondEventDelete : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        private int _deleteCount;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("DELETE FROM \"discord_member_events\"", StringComparison.Ordinal)
                && ++_deleteCount == 2)
                throw new InvalidOperationException("Interrupted history purge.");
            return ValueTask.FromResult(result);
        }
    }

    private async Task SeedAccountAsync(string userId, DateTime lastSeen)
    {
        _db.DiscordAccounts.Add(new DiscordAccount
        {
            DiscordUserId = userId, Username = $"user{userId[^4..]}", FirstSeenAt = lastSeen.AddDays(-100), LastSeenAt = lastSeen,
        });
        await SaveAsync();
    }

    private async Task SeedMemberAsync(string guildId, string userId, DateTime lastSeen, DateTime? leftAt)
    {
        _db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = guildId, DiscordUserId = userId, RoleIdsJson = "[]",
            FirstSeenAt = lastSeen.AddDays(-100), LastSeenAt = lastSeen, LeftAt = leftAt,
        });
        await SaveAsync();
    }

    private async Task SeedEventAsync(string? guildId, string userId)
    {
        _db.DiscordMemberEvents.Add(new DiscordMemberEvent
        {
            GuildId = guildId, DiscordUserId = userId, SyncId = 1, Type = DiscordEventTypes.Joined, ObservedAt = Old,
        });
        await SaveAsync();
    }

    private async Task SeedRejectionAsync(string userId)
    {
        _db.DiscordLinkRejections.Add(new DiscordLinkRejection
        {
            DiscordUserId = userId, CitizenKey = "h:someone", ByApiUserId = 1, ByUsername = "user", CreatedAt = Old,
        });
        await SaveAsync();
    }

    private async Task SeedLinkAsync(string userId)
    {
        var entity = new TrackedEntity { CurrentHandle = $"linked{userId[^4..]}", CreatedAt = Old, UpdatedAt = Old };
        _db.TrackedEntities.Add(entity);
        await _db.SaveChangesAsync();
        _db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = userId,
            AuthorApiUserId = 1, AuthorUsername = "user", CreatedAt = Old, UpdatedAt = Old,
        });
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }
}
