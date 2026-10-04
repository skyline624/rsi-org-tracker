using System.Text.RegularExpressions;
using Collector.Data;
using Collector.Data.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// The API's hot queries, as EF generates them, are answered from an index: no full
/// scan of the large tables and no temporary sort.
/// </summary>
public sealed class QueryPlanTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }

    /// <summary>EXPLAIN QUERY PLAN of the SQL EF generates, with its parameters bound.</summary>
    private async Task<string> PlanAsync(IQueryable query)
    {
        var lines = query.ToQueryString().Split('\n');
        await using var command = _connection.CreateCommand();
        foreach (var line in lines.Where(l => l.StartsWith(".param set ")))
        {
            var match = Regex.Match(line.Trim(), @"^\.param set (\S+) (.+)$");
            var raw = match.Groups[2].Value;
            object value = raw.StartsWith('\'') ? raw[1..^1].Replace("''", "'") : long.Parse(raw);
            command.Parameters.AddWithValue(match.Groups[1].Value, value);
        }
        command.CommandText = "EXPLAIN QUERY PLAN " + string.Join('\n', lines.Where(l => !l.StartsWith(".param set ")));
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return string.Join(" | ", plan);
    }

    [Fact]
    public async Task ChangesByType_NewestFirst_UsesTheTypeIndexWithoutSorting()
    {
        var plan = await PlanAsync(_db.ChangeEvents.Where(c => c.ChangeType == "member_joined").OrderByDescending(c => c.Id).Take(100));

        plan.Should().Contain("USING INDEX IX_change_events_ChangeType").And.NotContain("TEMP B-TREE");
    }

    [Fact]
    public async Task RecentChanges_ReadTheTableBackwardsWithoutSorting()
    {
        (await PlanAsync(_db.ChangeEvents.OrderByDescending(c => c.Id).Take(100))).Should().NotContain("TEMP B-TREE");
    }

    [Fact]
    public async Task CurrentMembersOfAnOrg_UseAnOrgSidIndex()
    {
        var plan = await PlanAsync(_db.OrganizationMembers.Where(m => m.OrgSid == "TEST" && m.IsActive));

        plan.Should().MatchRegex("USING (COVERING )?INDEX IX_organization_members_OrgSid_").And.NotContain("SCAN");
    }

    [Fact]
    public async Task OrgNotesOfAnOrg_UseTheOrgSidIndex()
    {
        (await PlanAsync(_db.OrgNotes.Where(n => n.OrgSid == "TEST"))).Should().Contain("USING INDEX IX_org_notes_OrgSid");
    }

    [Fact]
    public async Task CountingTheDueQueue_SeeksThePendingIndex()
    {
        // Phase 4 counts it before every batch and the dashboard on every refresh: "!Enriched"
        // became NOT (Enriched), which scanned the whole queue.
        var plan = await PlanAsync(UserEnrichmentQueueRepository.PendingDue(_db.UserEnrichmentQueue, DateTime.UtcNow));

        plan.Should().MatchRegex("USING (COVERING )?INDEX IX_user_enrichment_queue_Enriched_Priority_QueuedAt \\(Enriched=\\?");
    }

    [Fact]
    public async Task ProfilesToRefresh_WalkThePrimaryKeyFromTheCursor()
    {
        // Phase 4 asks for the next few every few seconds while hundreds of thousands wait.
        var plan = await PlanAsync(UserRepository.ProfilesToRefresh(_db.Users, afterId: 1000, version: 2).Take(10));

        plan.Should().Contain("USING INTEGER PRIMARY KEY (rowid>?)").And.NotContain("TEMP B-TREE");
    }

    [Fact]
    public async Task QueueDueRows_ReadThePendingIndexInOrder()
    {
        var plan = await PlanAsync(UserEnrichmentQueueRepository.DueQuery(_db.UserEnrichmentQueue, 1, DateTime.UtcNow).Take(10));

        // Measured on the production copy: 150-330 ms per Phase 4 batch with a sort, 0 ms without.
        plan.Should().Contain("USING INDEX IX_user_enrichment_queue_Enriched_Priority_QueuedAt (Enriched=? AND Priority=?)")
            .And.NotContain("TEMP B-TREE");
    }

    [Fact]
    public async Task OrgMovements_SeekTheOrgSidTimestampIndex()
    {
        var plan = await PlanAsync(ChangeEventRepository.MovementsQuery(
            _db.ChangeEvents, "TEST", DateTime.UtcNow.AddDays(-7), "member_left").Take(51));

        plan.Should().Contain("IX_change_events_OrgSid_Timestamp").And.NotContain("TEMP B-TREE");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }
}
