using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Api.Auth;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Review focus 4: a 20,000-member guild. The guild list never looks a handle up (no
/// suggestion is computed), and a members page is cut by the database in a bounded number
/// of queries, rank and status filters included.
/// </summary>
public sealed class DiscordRosterQueryCostTests : IAsyncLifetime
{
    private const int MemberCount = 20_000;
    private const string GuildId = "400000000000000001";
    private const string OfficerRoleId = "400000000000000011";
    private const string PilotRoleId = "400000000000000012";
    private const string FlairRoleId = "400000000000000013";
    private const string LinkedUserId = "500000000000000007";
    private static readonly DateTime At = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    // Member i has the id 500000000000000000 + i and the username "member" + i on 5 digits.
    private const string AccountsSql = """
        INSERT INTO discord_accounts (DiscordUserId, Username, GlobalName, IsBot, FirstSeenAt, LastSeenAt)
        WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 20000)
        SELECT CAST(500000000000000000 + i AS TEXT), printf('member%05d', i), NULL, 0,
               '2026-09-01 12:00:00', '2026-09-01 12:00:00'
        FROM seq;
        """;

    // Every 4th member is an officer who also wears the pilot role; the others are pilots,
    // one in twenty with the flair role (not a rank).
    private const string MembersSql = """
        INSERT INTO discord_members (GuildId, DiscordUserId, Nick, RoleIdsJson, JoinedAt, FirstSeenAt, LastSeenAt, LeftAt)
        WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 20000)
        SELECT '400000000000000001', CAST(500000000000000000 + i AS TEXT), NULL,
               CASE WHEN i % 4 = 0 THEN '["400000000000000011","400000000000000012"]'
                    WHEN i % 20 = 10 THEN '["400000000000000012","400000000000000013"]'
                    ELSE '["400000000000000012"]' END,
               NULL, '2026-09-01 12:00:00', '2026-09-01 12:00:00', NULL
        FROM seq;
        """;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
        await _db.Database.MigrateAsync();

        _db.Organizations.Add(new Organization { Sid = "BIG", Name = "Big corpo", Timestamp = At });
        // The roster is known (no rsi_unknown); one of its members is linked to member 7.
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "BIG", UserHandle = "Linked7", CitizenId = 900_007, Rank = "Pilot", Timestamp = At, IsActive = true },
            new OrganizationMember { OrgSid = "BIG", UserHandle = "RosterOnly", CitizenId = 900_008, Rank = "Pilot", Timestamp = At, IsActive = true });
        _db.DiscordGuilds.Add(new DiscordGuild
        {
            GuildId = GuildId, Name = "Big guild", OrgSid = "BIG", FirstSyncAt = At, LastSyncAt = At, LastCollectedAt = At,
            LastCompleteSyncAt = At, CreatedAt = At, UpdatedAt = At,
        });
        _db.DiscordRoles.AddRange(
            Role(OfficerRoleId, "Officier", 20, isRank: true),
            Role(PilotRoleId, "Pilote", 10, isRank: true),
            Role(FlairRoleId, "Flair", 30, isRank: false));
        _db.DiscordSyncs.Add(new DiscordSync
        {
            GuildId = GuildId, SubmittedByApiUserId = 1, SubmittedByUsername = "sender", ReceivedAt = At, CollectedAt = At,
            DeclaredCollectedAt = At, Method = DiscordSyncMethods.MemberSearch, DeclaredComplete = true, IsComplete = true,
            ExpectedCount = MemberCount, CollectedCount = MemberCount, PluginVersion = "1.0.0",
        });
        var linked = new TrackedEntity { CitizenId = 900_007, CurrentHandle = "Linked7", CreatedAt = At, UpdatedAt = At };
        _db.TrackedEntities.Add(linked);
        await _db.SaveChangesAsync();
        _db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = linked.Id, Provider = LinkProviders.Discord, Value = LinkedUserId,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = At, UpdatedAt = At,
        });
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync(AccountsSql);
        await _db.Database.ExecuteSqlRawAsync(MembersSql);
        _db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task TheGuildList_ReadsNoHandle_AndTakesAFixedNumberOfQueries()
    {
        _sql.Commands.Clear();

        var guilds = await Service().ListGuildsAsync(CancellationToken.None);

        var guild = guilds.Should().ContainSingle().Subject;
        guild.ActiveMembers.Should().Be(MemberCount);
        guild.RankDistribution.Select(r => (r.RoleId, r.Count)).Should().Equal((OfficerRoleId, 5_000), (PilotRoleId, 15_000));
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(5);
        _sql.Commands.Should().NotContain(c =>
            c.Contains("\"users\"") || c.Contains("\"user_handle_history\"") || c.Contains("\"organization_members\"")
            || c.Contains("\"entity_links\"") || c.Contains("\"discord_link_rejections\""),
            "the guild list never computes link suggestions");
    }

    [Fact]
    public async Task AMembersPage_IsCutByTheDatabase()
    {
        _sql.Commands.Clear();

        var page = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, null, null, Page: 3, PageSize: 50), CancellationToken.None);

        page.Total.Should().Be(MemberCount);
        page.Page.Should().Be(3);
        page.PageSize.Should().Be(50);
        page.Items.Select(m => m.Username).Should().Equal(Enumerable.Range(101, 50).Select(i => $"member{i:D5}"));
        page.Items.Should().OnlyContain(m => m.Rank != null);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(8);
        _sql.Commands.Should().Contain(c => c.Contains("\"discord_members\"") && c.Contains("LIMIT"));
    }

    [Theory]
    [InlineData(OfficerRoleId, 5_000)]
    [InlineData(PilotRoleId, 15_000)]
    public async Task ARankFilter_IsOneBoundedPass(string rankRoleId, int expected)
    {
        _sql.Commands.Clear();

        var page = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, rankRoleId, null, Page: 2, PageSize: 50), CancellationToken.None);

        // Officers also wear the pilot role: the pilot filter keeps only members whose rank it is.
        page.Total.Should().Be(expected);
        page.Items.Should().HaveCount(50).And.OnlyContain(m => m.Rank!.RoleId == rankRoleId);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(8);
        _sql.Commands.Should().Contain(c => c.Contains("\"discord_members\"") && c.Contains("LIKE"));
    }

    [Fact]
    public async Task AReconciliationFilter_IsOneBoundedPass()
    {
        _sql.Commands.Clear();
        var ok = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, null, "ok", Page: 1, PageSize: 50), CancellationToken.None);
        var okQueries = _sql.Commands.Count;
        _sql.Commands.Clear();

        var unlinked = await Service().GetMembersAsync(
            GuildId, new DiscordMemberQuery("active", null, null, "unlinked", Page: 1, PageSize: 50), CancellationToken.None);

        ok.Total.Should().Be(1);
        var linked = ok.Items.Should().ContainSingle().Subject;
        linked.DiscordUserId.Should().Be(LinkedUserId);
        linked.Links.Should().ContainSingle().Which.Handle.Should().Be("Linked7");
        linked.RsiRank.Should().Be("Pilot");
        unlinked.Total.Should().Be(MemberCount - 1);
        unlinked.Items.Should().HaveCount(50).And.OnlyContain(m => m.Reconciliation == "unlinked");
        okQueries.Should().BeLessThanOrEqualTo(8);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(8);
    }

    private DiscordRosterQueryService Service() => new(
        _db,
        new DiscordReconciliationService(_db),
        new OrganizationRepository(_db),
        new CurrentUserAccessor(new HttpContextAccessor()));

    private static DiscordRole Role(string roleId, string name, int position, bool isRank) => new()
    {
        GuildId = GuildId, RoleId = roleId, Name = name, Position = position, Color = null,
        Hoist = isRank, Managed = false, IsRank = isRank, RankOrder = isRank ? position : null,
        FirstSeenAt = At, LastSeenAt = At,
    };

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
