using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Discord name tokens are looked up among RSI handles regardless of case, in bounded
/// batches served by the NOCASE indexes, and always come back with the canonical current
/// handle and the citizen id read from the database.
/// </summary>
public sealed class DiscordHandleLookupTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Tokens_GoInBatchesOf500_ThroughTheNoCaseIndexes()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            new User { CitizenId = 1, UserHandle = "Batch0003", CreatedAt = now, UpdatedAt = now },
            new User { CitizenId = 2, UserHandle = "Renamed1199", CreatedAt = now, UpdatedAt = now });
        _db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = 2, UserHandle = "Batch1199", FirstSeen = now.AddYears(-2), LastSeen = now.AddYears(-1),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var tokens = Enumerable.Range(0, 1200).Select(i => $"batch{i:D4}").ToList();
        _sql.Commands.Clear();

        var matches = await new DiscordHandleLookup(_db).FindAsync(tokens, rosterOrgSids: [], default);

        matches.Should().BeEquivalentTo(new[]
        {
            new HandleMatch("Batch0003", "Batch0003", 1, null, RosterOrgSid: null),
            new HandleMatch("Batch1199", "Renamed1199", 2, null, RosterOrgSid: null),
        });
        var lookups = _sql.Commands.Where(c => c.Sql.Contains("COLLATE NOCASE IN (")).ToList();
        lookups.Where(c => c.Sql.Contains("FROM users")).Should().HaveCount(3);
        lookups.Where(c => c.Sql.Contains("FROM user_handle_history")).Should().HaveCount(3);
        lookups.Should().OnlyContain(c => c.Parameters.Count <= DiscordHandleLookup.BatchSize);
        lookups.Max(c => c.Parameters.Count).Should().Be(DiscordHandleLookup.BatchSize);
        (await PlanAsync(lookups.First(c => c.Sql.Contains("FROM users"))))
            .Should().Contain(d => d.Contains("IX_users_UserHandle_NoCase"));
        (await PlanAsync(lookups.First(c => c.Sql.Contains("FROM user_handle_history"))))
            .Should().Contain(d => d.Contains("IX_user_handle_history_UserHandle_NoCase"));
    }

    [Fact]
    public async Task Matches_CarryTheCanonicalCurrentHandle_AndRosterMatchesNameTheirCorpo()
    {
        var now = DateTime.UtcNow;
        _db.Users.AddRange(
            new User { CitizenId = 10, UserHandle = "Current10", DisplayName = "Ten", CreatedAt = now, UpdatedAt = now },
            new User { CitizenId = 30, UserHandle = "Rostered30", DisplayName = "Thirty", CreatedAt = now, UpdatedAt = now });
        _db.UserHandleHistories.AddRange(
            new UserHandleHistory { CitizenId = 10, UserHandle = "Former10", FirstSeen = now.AddYears(-3), LastSeen = now.AddYears(-2) },
            new UserHandleHistory { CitizenId = 20, UserHandle = "OnlyOld20", FirstSeen = now.AddYears(-3), LastSeen = now.AddYears(-2) },
            new UserHandleHistory { CitizenId = 20, UserHandle = "OnlyNew20", FirstSeen = now.AddYears(-1), LastSeen = now });
        var formerMember = new OrganizationMember { OrgSid = "ORG", UserHandle = "Current10", CitizenId = 10, Timestamp = now, IsActive = true };
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORG", UserHandle = "Rostered30", CitizenId = null, Timestamp = now, IsActive = true },
            formerMember);
        await _db.SaveChangesAsync();
        // IsActive has a database default of true: EF omits an inserted false (the CLR default)
        // and the row would come back active. Insert it active, then update it.
        formerMember.IsActive = false;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var matches = await new DiscordHandleLookup(_db)
            .FindAsync(["former10", "onlyold20", "ROSTERED30", "current10"], ["ORG"], default);

        matches.Should().BeEquivalentTo(new[]
        {
            // A former handle: the citizen's current handle, from users.
            new HandleMatch("Former10", "Current10", 10, "Ten", RosterOrgSid: null),
            // A former handle of a citizen without a users row: their latest handle.
            new HandleMatch("OnlyOld20", "OnlyNew20", 20, null, RosterOrgSid: null),
            // An active roster row names its corpo; the citizen id comes from users.
            new HandleMatch("Rostered30", "Rostered30", 30, "Thirty", RosterOrgSid: "ORG"),
            new HandleMatch("Rostered30", "Rostered30", 30, "Thirty", RosterOrgSid: null),
            // An inactive roster row is not a roster match.
            new HandleMatch("Current10", "Current10", 10, "Ten", RosterOrgSid: null),
        });
    }

    [Fact]
    public async Task AnActiveRosterWithAFormerHandle_ReturnsTheCurrentCitizenHandle()
    {
        var now = DateTime.UtcNow;
        _db.Users.Add(new User
        {
            CitizenId = 90, UserHandle = "Current90", DisplayName = "Ninety", CreatedAt = now, UpdatedAt = now,
        });
        _db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = "ORG", UserHandle = "Former90", CitizenId = 90, Timestamp = now.AddDays(-1), IsActive = true,
        });
        await _db.SaveChangesAsync();

        var matches = await new DiscordHandleLookup(_db).FindAsync(["former90"], ["ORG"], default);

        matches.Should().ContainSingle().Which.Should()
            .Be(new HandleMatch("Former90", "Current90", 90, "Ninety", RosterOrgSid: "ORG"));
    }

    [Fact]
    public async Task SeveralRosters_AreReadTogether_AndEachMatchNamesItsOwnCorpo()
    {
        var now = DateTime.UtcNow;
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "HOME", UserHandle = "Homer", CitizenId = 41, Timestamp = now, IsActive = true },
            new OrganizationMember { OrgSid = "ALLY", UserHandle = "Allied", CitizenId = 42, Timestamp = now, IsActive = true },
            new OrganizationMember { OrgSid = "ELSE", UserHandle = "Elsewhere", CitizenId = 43, Timestamp = now, IsActive = true });
        await _db.SaveChangesAsync();

        var matches = await new DiscordHandleLookup(_db).FindAsync(["homer", "allied", "elsewhere"], ["HOME", "ALLY"], default);

        matches.Should().BeEquivalentTo(new[]
        {
            new HandleMatch("Homer", "Homer", 41, null, RosterOrgSid: "HOME"),
            new HandleMatch("Allied", "Allied", 42, null, RosterOrgSid: "ALLY"),
        });
    }

    [Fact]
    public async Task KnownOrgs_KeepsTheSidsAnOrganizationHas_WithItsLatestName()
    {
        var now = DateTime.UtcNow;
        _db.Organizations.AddRange(
            new Organization { Sid = "ABC", Name = "Old name", Timestamp = now.AddDays(-2) },
            new Organization { Sid = "ABC", Name = "New name", Timestamp = now },
            new Organization { Sid = "DEF", Name = "Other", Timestamp = now });
        await _db.SaveChangesAsync();

        var known = await new DiscordHandleLookup(_db).KnownOrgsAsync(["ABC", "XYZ"], default);

        known.Should().BeEquivalentTo(new Dictionary<string, string> { ["ABC"] = "New name" });
    }

    [Fact]
    public async Task APerson_IsReadBackRegardlessOfCase_FromUsersThenHistoryThenTheRoster()
    {
        var now = DateTime.UtcNow;
        _db.Users.Add(new User { CitizenId = 40, UserHandle = "Pilote40", CreatedAt = now, UpdatedAt = now });
        _db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = 40, UserHandle = "OldPilote40", FirstSeen = now.AddYears(-2), LastSeen = now.AddYears(-1),
        });
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORG", UserHandle = "Roster50", CitizenId = null, Timestamp = now, IsActive = true },
            new OrganizationMember { OrgSid = "ORG", UserHandle = "Roster60", CitizenId = 60, Timestamp = now, IsActive = true });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var lookup = new DiscordHandleLookup(_db);

        (await lookup.ResolvePersonAsync(null, "pilote40", default)).Should().Be(new RsiPerson(40, "Pilote40", null));
        (await lookup.ResolvePersonAsync(null, "OLDPILOTE40", default)).Should().Be(new RsiPerson(40, "Pilote40", null));
        (await lookup.ResolvePersonAsync(40, "anything", default)).Should().Be(new RsiPerson(40, "Pilote40", null));
        (await lookup.ResolvePersonAsync(null, "Roster50", default)).Should().Be(new RsiPerson(null, "Roster50", null));
        // The roster's own spelling comes back, whatever the case sent.
        (await lookup.ResolvePersonAsync(null, "ROSTER50", default)).Should().Be(new RsiPerson(null, "Roster50", null));
        (await lookup.ResolvePersonAsync(60, "Roster60", default)).Should().Be(new RsiPerson(60, "Roster60", null));
        (await lookup.ResolvePersonAsync(null, "Nobody", default)).Should().BeNull();
    }

    private async Task<IReadOnlyList<string>> PlanAsync(CapturedCommand command)
    {
        await using var explain = _connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.Sql;
        foreach (var (name, value) in command.Parameters) explain.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var details = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return details;
    }

    private sealed record CapturedCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<CapturedCommand> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(new CapturedCommand(
                command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToList()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
