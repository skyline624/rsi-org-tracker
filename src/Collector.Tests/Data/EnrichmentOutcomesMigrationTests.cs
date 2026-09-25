using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>The EnrichmentOutcomesAndContentChecks migration on rows written by the previous code.</summary>
public sealed class EnrichmentOutcomesMigrationTests : IDisposable
{
    private const string Before = "20260925144042_AddRosterCountsAndParserVersion";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TrackerDbContext _db;

    public EnrichmentOutcomesMigrationTests()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
    }

    [Fact]
    public async Task LegacyQueueRowsAndContentSnapshots_AreConverted()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO user_enrichment_queue (UserHandle, Priority, Enriched, QueuedAt, AttemptCount, LastError) VALUES
              ('gone', 0, 0, '2026-09-01 00:00:00', 2147483647, 'Gone (HTTP 404)'),
              ('abandoned', 0, 0, '2026-09-02 00:00:00', 3, 'Fetch failed (throttle/network)'),
              ('na', 0, 0, '2026-09-03 00:00:00', 0, 'No citizen record (n/a)'),
              ('waiting', 1, 0, '2026-09-04 00:00:00', 1, 'Fetch failed (throttle/network)');
            INSERT INTO discovered_organizations (Sid, Name, DiscoveredAt) VALUES
              ('READ', 'Read', '2026-01-01 00:00:00'),
              ('NEVER', 'Never', '2026-01-01 00:00:00');
            INSERT INTO organizations (Sid, Name, Timestamp, MembersCount, ContentCollected) VALUES
              ('READ', 'Read', '2026-09-10 00:00:00', 1, 1),
              ('READ', 'Read', '2026-09-20 00:00:00', 1, 0),
              ('NEVER', 'Never', '2026-09-20 00:00:00', 1, 0);
            """);

        await migrator.MigrateAsync();

        var queue = await _db.UserEnrichmentQueue.AsNoTracking().ToDictionaryAsync(q => q.UserHandle);
        queue["gone"].Should().Match<UserEnrichmentQueue>(q => q.Enriched && q.Outcome == EnrichmentOutcome.Gone);
        queue["abandoned"].Should().Match<UserEnrichmentQueue>(q => q.Enriched && q.Outcome == EnrichmentOutcome.Abandoned);
        queue["na"].Outcome.Should().Be(EnrichmentOutcome.NoCitizenRecord);
        queue["na"].NextAttemptAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1)).And.BeBefore(DateTime.UtcNow.AddDays(14));
        queue["waiting"].Should().Match<UserEnrichmentQueue>(q => !q.Enriched && q.Outcome == null && q.NextAttemptAt == null);

        var checkedAt = await _db.DiscoveredOrganizations.Select(d => new { d.Sid, d.ContentCheckedAt })
            .ToDictionaryAsync(d => d.Sid, d => d.ContentCheckedAt);
        checkedAt["READ"].Should().Be(new DateTime(2026, 9, 10));
        checkedAt["NEVER"].Should().BeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
