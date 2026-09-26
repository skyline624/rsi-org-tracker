using Collector.Data;
using Collector.Extensions;
using Collector.Models;
using Collector.Services;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>Batched purges run by <c>Collector --maintenance</c>, collector stopped.</summary>
public sealed class MaintenanceServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeStorage _storage = new();
    private readonly ListLogger<MaintenanceService> _logger = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    private MaintenanceService Create() => new(NewDb(), _storage, _logger);

    private async Task AddEventsAsync(params ChangeEvent[] events)
    {
        var db = NewDb();
        foreach (var e in events)
        {
            e.Timestamp = e.Timestamp == default ? Now : e.Timestamp;
            e.EntityType ??= "organization";
            e.EntityId ??= e.OrgSid ?? "X";
            db.ChangeEvents.Add(e);
            await db.SaveChangesAsync();
        }
    }

    private async Task<List<string>> EventsLeftAsync()
        => await NewDb().ChangeEvents.OrderBy(e => e.Id).Select(e => e.ChangeType + ":" + (e.OldValue ?? "null") + ">" + e.NewValue).ToListAsync();

    private Task SeedContentEventsAsync() => AddEventsAsync(
        new ChangeEvent { ChangeType = "description_changed", OrgSid = "A", NewValue = "a" },
        new ChangeEvent { ChangeType = "charter_changed", OrgSid = "A", NewValue = "b" },
        new ChangeEvent { ChangeType = "focus_primary_changed", OrgSid = "B", NewValue = "c" },
        new ChangeEvent { ChangeType = "description_changed", OrgSid = "B", OldValue = "real", NewValue = "d" },
        new ChangeEvent { ChangeType = "member_joined", OrgSid = "B", NewValue = "e" });

    [Fact]
    public async Task DryRun_CountsWithoutDeleting()
    {
        await SeedContentEventsAsync();

        var report = await Create().PurgeAsync("content-null-events", dryRun: true, batchSize: 2, Now);

        report.Matched.Should().Be(3);
        report.Deleted.Should().Be(0);
        (await EventsLeftAsync()).Should().HaveCount(5);
    }

    [Fact]
    public async Task ContentEventsWithoutOldValue_AreDeletedInBatches()
    {
        await SeedContentEventsAsync();

        var report = await Create().PurgeAsync("content-null-events", dryRun: false, batchSize: 2, Now);

        report.Deleted.Should().Be(3);
        report.Batches.Should().Be(2);
        (await EventsLeftAsync()).Should().Equal("description_changed:real>d", "member_joined:null>e");
    }

    [Fact]
    public async Task MemberCountEvents_RepeatingTheOrgsPreviousValue_AreDeleted()
    {
        await AddEventsAsync(
            new ChangeEvent { ChangeType = "member_count_changed", OrgSid = "A", OldValue = "9", NewValue = "10" },
            new ChangeEvent { ChangeType = "member_count_changed", OrgSid = "B", OldValue = "1", NewValue = "10" },
            new ChangeEvent { ChangeType = "member_count_changed", OrgSid = "A", OldValue = "12", NewValue = "12" },
            new ChangeEvent { ChangeType = "member_count_changed", OrgSid = "A", OldValue = "12", NewValue = "12" },
            new ChangeEvent { ChangeType = "member_count_changed", OrgSid = "A", OldValue = "12", NewValue = "11" });

        var report = await Create().PurgeAsync("member-count-repeats", dryRun: false, batchSize: 100, Now);

        report.Deleted.Should().Be(1);
        (await EventsLeftAsync()).Should().Equal(
            "member_count_changed:9>10", "member_count_changed:1>10", "member_count_changed:12>12", "member_count_changed:12>11");
    }

    [Fact]
    public async Task QueueRows_AreDeletedPastTheirRetention()
    {
        var db = NewDb();
        UserEnrichmentQueue Row(string handle, string? outcome, int daysAgo) => new()
        {
            UserHandle = handle, QueuedAt = Now.AddDays(-daysAgo - 1), Enriched = true,
            EnrichedAt = Now.AddDays(-daysAgo), Outcome = outcome,
        };
        db.UserEnrichmentQueue.AddRange(
            Row("enriched-8d", EnrichmentOutcome.Enriched, 8),
            Row("legacy-8d", null, 8),
            Row("enriched-6d", EnrichmentOutcome.Enriched, 6),
            Row("gone-100d", EnrichmentOutcome.Gone, 100),
            Row("abandoned-80d", EnrichmentOutcome.Abandoned, 80),
            new UserEnrichmentQueue { UserHandle = "pending", QueuedAt = Now.AddYears(-1) });
        await db.SaveChangesAsync();

        (await Create().PurgeAsync("queue-enriched", dryRun: false, batchSize: 100, Now)).Deleted.Should().Be(2);
        (await Create().PurgeAsync("queue-terminal", dryRun: false, batchSize: 100, Now)).Deleted.Should().Be(1);

        (await NewDb().UserEnrichmentQueue.OrderBy(q => q.UserHandle).Select(q => q.UserHandle).ToListAsync())
            .Should().Equal("abandoned-80d", "enriched-6d", "pending");
    }

    [Fact]
    public async Task LowFreeDisk_StopsBeforeDeleting()
    {
        await SeedContentEventsAsync();
        _storage.FreeBytes = 2L << 30;

        var report = await Create().PurgeAsync("content-null-events", dryRun: false, batchSize: 2, Now);

        report.Deleted.Should().Be(0);
        report.StoppedBecause.Should().Contain("disk");
    }

    [Fact]
    public async Task AGrowingWal_IsCheckpointed_AndStopsThePurgeIfItStaysLarge()
    {
        await SeedContentEventsAsync();
        _storage.WalBytes = 2L << 30;
        _storage.CheckpointShrinksWal = false;

        var report = await Create().PurgeAsync("content-null-events", dryRun: false, batchSize: 2, Now);

        report.Deleted.Should().Be(2, "the first batch runs, then the WAL check stops the purge");
        report.StoppedBecause.Should().Contain("WAL");
    }

    [Fact]
    public async Task QuickCheck_OfAHealthyDatabase_IsOk()
    {
        (await Create().QuickCheckAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task RepairOrgNames_DecodesHtmlEntities_InEverySnapshot()
    {
        var db = NewDb();
        db.Organizations.AddRange(
            new Organization { Sid = "LH34", Name = "Les H&eacute;raults 34", Timestamp = Now.AddDays(-9) },
            new Organization { Sid = "LH34", Name = "Les H&eacute;raults 34", Timestamp = Now.AddDays(-2) },
            new Organization { Sid = "PLAIN", Name = "Plain & simple", Timestamp = Now.AddDays(-2) });
        db.DiscoveredOrganizations.Add(new DiscoveredOrganization { Sid = "LH34", Name = "Les H&eacute;raults 34", DiscoveredAt = Now });
        await db.SaveChangesAsync();

        (await Create().RepairOrgNamesAsync(dryRun: true)).Should().Be(3);
        (await NewDb().Organizations.CountAsync(o => o.Name.Contains("&eacute;"))).Should().Be(2, "a dry run changes nothing");

        (await Create().RepairOrgNamesAsync(dryRun: false)).Should().Be(3);

        (await NewDb().Organizations.OrderBy(o => o.Id).Select(o => o.Name).ToListAsync())
            .Should().Equal("Les Héraults 34", "Les Héraults 34", "Plain & simple");
        (await NewDb().DiscoveredOrganizations.Select(d => d.Name).SingleAsync()).Should().Be("Les Héraults 34");
    }

    [Fact]
    public async Task RepairOrgNames_DecodesNamesEncodedSeveralTimes_AndCanBeRunAgain()
    {
        var db = NewDb();
        db.Organizations.Add(new Organization { Sid = "AADHD", Name = "Defence &amp;amp;amp; Hauling", Timestamp = Now });
        await db.SaveChangesAsync();

        (await Create().RepairOrgNamesAsync(dryRun: false)).Should().Be(1);
        (await Create().RepairOrgNamesAsync(dryRun: false)).Should().Be(0, "a second run finds nothing left to decode");

        (await NewDb().Organizations.Select(o => o.Name).SingleAsync()).Should().Be("Defence & Hauling");
    }

    [Fact]
    public async Task UnknownTarget_IsRejected()
    {
        var act = () => Create().PurgeAsync("everything", dryRun: true, batchSize: 10, Now);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*content-null-events*");
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }

    private sealed class FakeStorage : IStorageProbe
    {
        public long WalBytes { get; set; }
        public long FreeBytes { get; set; } = 100L << 30;
        public bool CheckpointShrinksWal { get; set; } = true;

        public long GetWalBytes() => WalBytes;
        public long GetFreeDiskBytes() => FreeBytes;
        public void OnCheckpoint()
        {
            if (CheckpointShrinksWal) WalBytes = 0;
        }
    }
}
