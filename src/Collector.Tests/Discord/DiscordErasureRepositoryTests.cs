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
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// Spec § 13.2 on a real tracker.db file: what erasing an account or a guild deletes and keeps,
/// the opt-out lists, the mass-departure lift, bounded guild erasure and SQLITE_BUSY.
/// </summary>
public sealed class DiscordErasureRepositoryTests : IAsyncLifetime
{
    private const string Guild = "100000000000000001";
    private const string OtherGuild = "100000000000000002";
    private const string RoleA = "300000000000000001";
    private const string RoleB = "300000000000000002";

    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sct-erasure-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Unpooled, for the same reason as in DiscordRosterRepositoryTests: no inherited busy_timeout.</summary>
    private TrackerDbContext NewDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False")
            .AddInterceptors(interceptors)
            .Options);

    private DiscordErasureRepository Repository(TrackerDbContext db) =>
        new(db) { Time = new FakeTimeProvider(Now) };

    private async Task SeedAsync(params object[] rows)
    {
        await using var db = NewDb();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<T> ReadAsync<T>(Func<TrackerDbContext, Task<T>> query)
    {
        await using var db = NewDb();
        return await query(db);
    }

    private static string User(int n) => (200000000000000000L + n).ToString(CultureInfo.InvariantCulture);

    private static DiscordGuild GuildRow(string guildId) => new()
    {
        GuildId = guildId, Name = $"Guild {guildId}", FirstSyncAt = T0, LastSyncAt = T0, LastCollectedAt = T0,
        CreatedAt = T0, UpdatedAt = T0,
    };

    private static DiscordMember MemberRow(string guildId, int user) => new()
    {
        GuildId = guildId, DiscordUserId = User(user), FirstSeenAt = T0, LastSeenAt = T0,
    };

    private static DiscordAccount AccountRow(int user) => new()
    {
        DiscordUserId = User(user), Username = $"user{user}", FirstSeenAt = T0, LastSeenAt = T0,
    };

    private static DiscordMemberEvent EventRow(string? guildId, int user, string type) => new()
    {
        GuildId = guildId, DiscordUserId = User(user), SyncId = 1, Type = type, ObservedAt = T0,
    };

    private static DiscordLinkRejection RejectionRow(int user) => new()
    {
        DiscordUserId = User(user), CitizenKey = $"h:pilot{user}", ByApiUserId = 7, ByUsername = "officer", CreatedAt = T0,
    };

    private static DiscordSync SyncRow(string guildId) => new()
    {
        GuildId = guildId, SubmittedByApiUserId = 7, SubmittedByUsername = "officer", ReceivedAt = T0, CollectedAt = T0,
        DeclaredCollectedAt = T0, Method = DiscordSyncMethods.MemberSearch, PluginVersion = "1.0.0",
    };

    private static EntityLink DiscordLink(long entityId, int user) => new()
    {
        TrackedEntityId = entityId, Provider = LinkProviders.Discord, Value = User(user),
        AuthorApiUserId = 7, AuthorUsername = "officer", CreatedAt = T0, UpdatedAt = T0,
    };

    // ── Accounts ──

    [Fact]
    public async Task EraseAccount_DeletesEverythingStoredAboutTheAccount_AndOptsItOut()
    {
        await SeedAsync(
            MemberRow(Guild, 1), MemberRow(OtherGuild, 1), MemberRow(Guild, 2),
            AccountRow(1), AccountRow(2),
            EventRow(Guild, 1, DiscordEventTypes.Joined), EventRow(null, 1, DiscordEventTypes.UsernameChanged),
            EventRow(Guild, 2, DiscordEventTypes.Joined),
            RejectionRow(1), RejectionRow(2),
            DiscordLink(10, 1), DiscordLink(20, 2),
            new EntityLink
            {
                TrackedEntityId = 10, Provider = LinkProviders.Uex, Value = User(1),
                AuthorApiUserId = 7, AuthorUsername = "officer", CreatedAt = T0, UpdatedAt = T0,
            });
        await using (var db = NewDb())
        {
            await Repository(db).EraseAccountAsync(User(1), 42, "admin-user");
        }

        (await ReadAsync(db => db.DiscordMembers.Select(m => m.DiscordUserId).ToListAsync())).Should().Equal(User(2));
        (await ReadAsync(db => db.DiscordAccounts.Select(a => a.DiscordUserId).ToListAsync())).Should().Equal(User(2));
        (await ReadAsync(db => db.DiscordMemberEvents.Select(e => e.DiscordUserId).ToListAsync())).Should().Equal(User(2));
        (await ReadAsync(db => db.DiscordLinkRejections.Select(r => r.DiscordUserId).ToListAsync())).Should().Equal(User(2));
        (await ReadAsync(db => db.EntityLinks.Select(l => l.Provider + ":" + l.Value).ToListAsync()))
            .Should().BeEquivalentTo([$"discord:{User(2)}", $"uex:{User(1)}"], "only the account's Discord links go");
        (await ReadAsync(db => db.DiscordOptOuts.SingleAsync())).Should().BeEquivalentTo(new
        {
            DiscordUserId = User(1), CreatedAt = Now.UtcDateTime, ByApiUserId = (long?)42, ByUsername = "admin-user", Reason = (string?)null,
        });
    }

    [Fact]
    public async Task EraseAccount_AlwaysLeavesOneOptOut_EvenForAnUnknownAccount_OrASecondTime()
    {
        await using (var db = NewDb())
        {
            await Repository(db).EraseAccountAsync(User(9), null, "admin");
        }
        await using (var db = NewDb())
        {
            await new DiscordErasureRepository(db) { Time = new FakeTimeProvider(Now.AddDays(1)) }
                .EraseAccountAsync(User(9), 42, "someone-else");
        }

        (await ReadAsync(db => db.DiscordOptOuts.SingleAsync())).Should().BeEquivalentTo(new
        {
            DiscordUserId = User(9), CreatedAt = Now.UtcDateTime, ByApiUserId = (long?)null, ByUsername = "admin",
        }, "the first opt-out is kept as is");
    }

    /// <summary>Stops the second batch of event deletion, after the first 5,000 rows committed.</summary>
    private sealed class FailSecondEventDeletion(int failureAt = 2) : DbCommandInterceptor
    {
        private int _deletions;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
                && command.CommandText.Contains("discord_member_events", StringComparison.Ordinal)
                && ++_deletions == failureAt)
                throw new InvalidOperationException("interrupted event erasure");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task InterruptedAccountErasure_LeavesItsOptOut_AndFinishesInBoundedBatches()
    {
        await SeedAsync(AccountRow(1), MemberRow(Guild, 1), RejectionRow(1), DiscordLink(10, 1));
        await SeedAsync(Enumerable.Range(0, 12_001)
            .Select(_ => (object)EventRow(null, 1, DiscordEventTypes.UsernameChanged)).ToArray());
        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter, new FailSecondEventDeletion()))
        {
            var act = () => Repository(db).EraseAccountAsync(User(1), 42, "original-admin");
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("interrupted event erasure");
        }
        (await ReadAsync(db => db.DiscordMemberEvents.CountAsync())).Should().Be(7_001);
        (await ReadAsync(db => db.DiscordOptOuts.SingleAsync())).ByUsername.Should().Be("original-admin");

        await using (var db = NewDb(counter))
            await Repository(db).EraseAccountAsync(User(1), 43, "retry-admin");

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordErasureRepository.RowsPerTransaction);
        (await ReadAsync(db => db.DiscordMemberEvents.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordAccounts.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordMembers.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordLinkRejections.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.EntityLinks.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordOptOuts.SingleAsync())).ByUsername.Should().Be("original-admin");
    }

    // ── Guilds ──

    /// <summary>
    /// Guild: members 1 (only here, unlinked), 2 (also on OtherGuild) and 3 (only here, linked).
    /// OtherGuild: member 2. Every account has an account event and a rejection.
    /// </summary>
    private Task SeedTwoGuildsAsync() =>
        SeedAsync(
            GuildRow(Guild), GuildRow(OtherGuild),
            new DiscordRole { GuildId = Guild, RoleId = RoleA, Name = "Officier", FirstSeenAt = T0, LastSeenAt = T0 },
            new DiscordRole { GuildId = OtherGuild, RoleId = RoleB, Name = "Pilote", FirstSeenAt = T0, LastSeenAt = T0 },
            MemberRow(Guild, 1), MemberRow(Guild, 2), MemberRow(Guild, 3), MemberRow(OtherGuild, 2),
            AccountRow(1), AccountRow(2), AccountRow(3),
            EventRow(Guild, 1, DiscordEventTypes.Joined), EventRow(Guild, 3, DiscordEventTypes.Left),
            EventRow(OtherGuild, 2, DiscordEventTypes.Joined),
            EventRow(null, 1, DiscordEventTypes.UsernameChanged), EventRow(null, 2, DiscordEventTypes.UsernameChanged),
            EventRow(null, 3, DiscordEventTypes.GlobalNameChanged),
            RejectionRow(1), RejectionRow(2), RejectionRow(3),
            SyncRow(Guild), SyncRow(OtherGuild),
            DiscordLink(30, 3));

    [Fact]
    public async Task EraseGuild_DeletesItsRows_AndTheUnlinkedAccountsItOrphans()
    {
        await SeedTwoGuildsAsync();
        bool erased;
        await using (var db = NewDb())
        {
            erased = await Repository(db).EraseGuildAsync(Guild, exclude: false, 42, "admin-user");
        }

        erased.Should().BeTrue();
        (await ReadAsync(db => db.DiscordGuilds.Select(g => g.GuildId).ToListAsync())).Should().Equal(OtherGuild);
        (await ReadAsync(db => db.DiscordRoles.Select(r => r.GuildId).ToListAsync())).Should().Equal(OtherGuild);
        (await ReadAsync(db => db.DiscordSyncs.Select(s => s.GuildId).ToListAsync())).Should().Equal(OtherGuild);
        (await ReadAsync(db => db.DiscordMembers.Select(m => m.GuildId + ":" + m.DiscordUserId).ToListAsync()))
            .Should().Equal($"{OtherGuild}:{User(2)}");
        (await ReadAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.GuildId == Guild))).Should().BeFalse();
        (await ReadAsync(db => db.DiscordAccounts.Select(a => a.DiscordUserId).ToListAsync()))
            .Should().BeEquivalentTo([User(2), User(3)], "account 2 is still on OtherGuild and account 3 is linked");
        (await ReadAsync(db => db.DiscordMemberEvents.Where(e => e.GuildId == null).Select(e => e.DiscordUserId).ToListAsync()))
            .Should().BeEquivalentTo([User(2), User(3)]);
        (await ReadAsync(db => db.DiscordLinkRejections.Select(r => r.DiscordUserId).ToListAsync()))
            .Should().BeEquivalentTo([User(2), User(3)]);
        (await ReadAsync(db => db.DiscordGuildOptOuts.AnyAsync())).Should().BeFalse("the next sync is a new baseline");
    }

    [Fact]
    public async Task EraseGuild_KeepsALinkedAccountLeftWithoutMemberRows()
    {
        await SeedTwoGuildsAsync();
        await using (var db = NewDb())
        {
            await Repository(db).EraseGuildAsync(Guild, exclude: false, 42, "admin-user");
        }

        (await ReadAsync(db => db.DiscordMembers.AnyAsync(m => m.DiscordUserId == User(3)))).Should().BeFalse();
        (await ReadAsync(db => db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == User(3)))).Should().BeTrue();
        (await ReadAsync(db => db.DiscordMemberEvents.AnyAsync(e => e.GuildId == null && e.DiscordUserId == User(3)))).Should().BeTrue();
        (await ReadAsync(db => db.DiscordLinkRejections.AnyAsync(r => r.DiscordUserId == User(3)))).Should().BeTrue();
        (await ReadAsync(db => db.EntityLinks.AnyAsync(l => l.Value == User(3)))).Should().BeTrue();
    }

    [Fact]
    public async Task EraseGuild_WithExclusion_RecordsOneGuildOptOut_EvenForAGuildNeverSynced()
    {
        const string never = "100000000000000009";
        await SeedTwoGuildsAsync();
        bool excludedKnown, excludedNever, excludedAgain;
        await using (var db = NewDb())
        {
            var repository = Repository(db);
            excludedKnown = await repository.EraseGuildAsync(Guild, exclude: true, 42, "admin-user");
            excludedNever = await repository.EraseGuildAsync(never, exclude: true, null, "admin");
            excludedAgain = await repository.EraseGuildAsync(never, exclude: true, 42, "admin-user");
        }

        new[] { excludedKnown, excludedNever, excludedAgain }.Should().AllBeEquivalentTo(true);
        (await ReadAsync(db => db.DiscordGuilds.AnyAsync(g => g.GuildId == Guild))).Should().BeFalse();
        var optOuts = await ReadAsync(db => db.DiscordGuildOptOuts.OrderBy(o => o.Id).ToListAsync());
        optOuts.Should().BeEquivalentTo(new[]
        {
            new { GuildId = Guild, CreatedAt = Now.UtcDateTime, ByApiUserId = (long?)42, ByUsername = "admin-user" },
            new { GuildId = never, CreatedAt = Now.UtcDateTime, ByApiUserId = (long?)null, ByUsername = "admin" },
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task EraseGuild_OfAnUnknownGuild_WithoutExclusion_ReturnsFalse()
    {
        await SeedTwoGuildsAsync();
        await using var db = NewDb();

        (await Repository(db).EraseGuildAsync("100000000000000009", exclude: false, 42, "admin-user")).Should().BeFalse();
        (await db.DiscordGuilds.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task EraseGuild_OfALargeGuild_WritesInBoundedTransactions()
    {
        const int members = 2_500;
        await SeedAsync(GuildRow(Guild));
        await SeedAsync(Enumerable.Range(1, members).SelectMany(n => new object[]
        {
            MemberRow(Guild, n), AccountRow(n),
            EventRow(Guild, n, DiscordEventTypes.Joined), EventRow(Guild, n, DiscordEventTypes.RolesChanged),
            EventRow(Guild, n, DiscordEventTypes.NickChanged),
        }).ToArray());
        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter))
        {
            await Repository(db).EraseGuildAsync(Guild, exclude: false, 42, "admin-user");
        }

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordRosterRepository.MaxRowsPerTransaction);
        counter.Rows.Sum().Should().Be(members * 5 + 1, "every member, account and event row, and the guild row");
        (await ReadAsync(db => db.DiscordMembers.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordAccounts.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordMemberEvents.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task InterruptedGuildErasure_RetainsOrphanRetryPointers_AndBoundsTheirHistoryDeletion()
    {
        await SeedAsync(GuildRow(Guild), MemberRow(Guild, 1), AccountRow(1), RejectionRow(1));
        await SeedAsync(Enumerable.Range(0, 12_001)
            .Select(_ => (object)EventRow(null, 1, DiscordEventTypes.UsernameChanged)).ToArray());
        var counter = new SqliteRowsPerTransaction();
        await using (var db = NewDb(counter, new FailSecondEventDeletion(failureAt: 3)))
        {
            var act = () => Repository(db).EraseGuildAsync(Guild, exclude: true, 42, "original-admin");
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        // The first DELETE was empty guild history; 5,000 orphan events committed before the
        // next batch failed. Account and membership remain for the next run to find the orphan.
        (await ReadAsync(db => db.DiscordMemberEvents.CountAsync())).Should().Be(7_001);
        (await ReadAsync(db => db.DiscordMembers.CountAsync())).Should().Be(1);
        (await ReadAsync(db => db.DiscordAccounts.CountAsync())).Should().Be(1);
        (await ReadAsync(db => db.DiscordGuildOptOuts.SingleAsync())).ByUsername.Should().Be("original-admin");
        await using (var db = NewDb(counter))
            (await Repository(db).EraseGuildAsync(Guild, exclude: true, 43, "retry-admin")).Should().BeTrue();

        counter.Rows.Should().OnlyContain(rows => rows <= DiscordErasureRepository.RowsPerTransaction);
        (await ReadAsync(db => db.DiscordGuilds.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordMembers.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordAccounts.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordMemberEvents.CountAsync())).Should().Be(0);
        (await ReadAsync(db => db.DiscordLinkRejections.CountAsync())).Should().Be(0);
    }

    // ── Opt-out lists and the mass-departure lift ──

    [Fact]
    public async Task AccountOptOuts_AreListedNewestFirst_AndRemovedOnce()
    {
        await SeedAsync(
            new DiscordOptOut { DiscordUserId = User(1), CreatedAt = T0, ByUsername = "admin" },
            new DiscordOptOut { DiscordUserId = User(2), CreatedAt = T0.AddDays(1), ByUsername = "admin", Reason = "request" });
        await using var db = NewDb();
        var repository = Repository(db);

        (await repository.ListOptOutsAsync()).Select(o => o.DiscordUserId).Should().Equal(User(2), User(1));
        (await repository.RemoveOptOutAsync(User(1))).Should().BeTrue();
        (await repository.RemoveOptOutAsync(User(1))).Should().BeFalse();
        (await repository.ListOptOutsAsync()).Select(o => o.DiscordUserId).Should().Equal(User(2));
    }

    [Fact]
    public async Task GuildOptOuts_AreListedNewestFirst_AndRemovedOnce()
    {
        await SeedAsync(
            new DiscordGuildOptOut { GuildId = Guild, CreatedAt = T0, ByUsername = "admin" },
            new DiscordGuildOptOut { GuildId = OtherGuild, CreatedAt = T0.AddDays(1), ByUsername = "admin" });
        await using var db = NewDb();
        var repository = Repository(db);

        (await repository.ListGuildOptOutsAsync()).Select(o => o.GuildId).Should().Equal(OtherGuild, Guild);
        (await repository.RemoveGuildOptOutAsync(Guild)).Should().BeTrue();
        (await repository.RemoveGuildOptOutAsync(Guild)).Should().BeFalse();
        (await repository.ListGuildOptOutsAsync()).Select(o => o.GuildId).Should().Equal(OtherGuild);
    }


    // ── SQLITE_BUSY and registration ──

    [Fact]
    public async Task AnErasureOnALockedDatabase_ThrowsDiscordStoreBusy_AfterOneRetry()
    {
        await SeedAsync(MemberRow(Guild, 1), AccountRow(1));
        await using var locker = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        await locker.OpenAsync();
        await using (var begin = locker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }
        var waits = new List<TimeSpan>();
        await using var db = NewDb();
        var repository = new DiscordErasureRepository(db)
        {
            BusyTimeout = TimeSpan.FromSeconds(1),
            Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
        };

        var act = () => repository.EraseAccountAsync(User(1), 42, "admin-user");

        await act.Should().ThrowAsync<DiscordStoreBusyException>();
        waits.Should().Equal(TimeSpan.FromSeconds(2));
        await using (var rollback = locker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
        (await ReadAsync(check => check.DiscordAccounts.AnyAsync())).Should().BeTrue("nothing was erased");
    }

    [Fact]
    public void TheRepository_ComesWithTheDataServices()
    {
        using var provider = DataServices();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDiscordErasureRepository>().Should().BeOfType<DiscordErasureRepository>();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}
