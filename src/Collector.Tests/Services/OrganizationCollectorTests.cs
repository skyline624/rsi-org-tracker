using Collector.Data;
using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Extensions;
using Collector.Models;
using Collector.Options;
using Collector.Parsers;
using Collector.Services;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>Phases 1 and 2 against a real (in-memory) tracker schema, with RSI mocked.</summary>
public sealed class OrganizationCollectorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IRsiApiClient> _rsi = new();
    private readonly ListLogger<OrganizationCollector> _logger = new();
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

    private OrganizationCollector Create(TrackerDbContext db) => new(
        _rsi.Object,
        new DiscoveredOrganizationRepository(db),
        new OrganizationRepository(db),
        new ChangeEventRepository(db),
        new ChangeDetector(NullLogger<ChangeDetector>.Instance),
        new OrgPageHtmlParser(NullLogger<OrgPageHtmlParser>.Instance),
        _logger,
        Microsoft.Extensions.Options.Options.Create(new CollectorOptions
        {
            DiscoverSortMethods = ["active"],
            EmptyPagesThreshold = 1,
            MaxConcurrentRequests = 1,
        }));

    private async Task SeedAsync(string sid, string name, string? description, DateTime timestamp)
    {
        var db = NewDb();
        db.DiscoveredOrganizations.Add(new DiscoveredOrganization
        {
            Sid = sid, Name = name, DiscoveredAt = timestamp,
        });
        db.Organizations.Add(new Organization
        {
            Sid = sid, Name = name, Timestamp = timestamp, MembersCount = 10,
            ContentCollected = description != null, Description = description,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Phase1_LeavesNothingTracked_AndReportsTheChangesItWrote()
    {
        await SeedAsync("EXIST", "Old name", "A long description", DateTime.UtcNow.AddDays(-2));
        _rsi.Setup(r => r.GetOrganizationsAsync(It.IsAny<int>(), "", "active", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, string _, string _, int _, CancellationToken _) => page == 1
                ? [
                    new OrganizationData { Sid = "EXIST", Name = "New name", MembersCount = 10 },
                    new OrganizationData { Sid = "NEWORG", Name = "Fresh", MembersCount = 3 },
                ]
                : []);
        var db = NewDb();

        await Create(db).DiscoverOrganizationsAsync();

        db.ChangeTracker.Entries().Should().BeEmpty("phase 1 writes ~100k snapshots per cycle");
        var written = await NewDb().ChangeEvents.CountAsync();
        written.Should().BeGreaterThan(0);
        _logger.Messages.Should().Contain(m => m.StartsWith("Discovery complete") && m.Contains($"{written} changes"));
    }

    [Fact]
    public async Task Phase2_LeavesNothingTracked_AndReportsTheContentChangesItWrote()
    {
        await SeedAsync("PAGE", "Page org", "Before", DateTime.UtcNow.AddDays(-30));
        _rsi.Setup(r => r.GetOrgPageHtmlAsync("PAGE", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgPageFetchResult(
                """<div class="content join-us"><div class="body markitup-text">After</div></div>""",
                OrgPageFetchOutcome.Ok));
        var db = NewDb();

        var processed = await Create(db).CollectOrganizationMetadataAsync();

        processed.Should().Be(1);
        db.ChangeTracker.Entries().Should().BeEmpty();
        var events = await NewDb().ChangeEvents.Select(e => e.ChangeType).ToListAsync();
        events.Should().Equal("description_changed");
        _logger.Messages.Should().Contain(m => m.StartsWith("Phase 2 complete") && m.Contains("1 content changes"));
    }

    private void ListOnPageOne(params OrganizationData[] orgs)
        => _rsi.Setup(r => r.GetOrganizationsAsync(It.IsAny<int>(), "", "active", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, string _, string _, int _, CancellationToken _) => page == 1 ? orgs : []);

    private void ServePage(string sid, string description)
        => _rsi.Setup(r => r.GetOrgPageHtmlAsync(sid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgPageFetchResult(
                $"""<div class="content join-us"><div class="body markitup-text">{description}</div></div>""",
                OrgPageFetchOutcome.Ok));

    private async Task<List<Organization>> SnapshotsAsync(string sid)
        => await NewDb().Organizations.AsNoTracking().Where(o => o.Sid == sid).OrderBy(o => o.Timestamp).ToListAsync();

    private async Task<List<ChangeEvent>> EventsAsync()
        => await NewDb().ChangeEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();

    [Fact]
    public async Task Phase1_AnUnchangedListing_WritesNothing()
    {
        await SeedAsync("SAME", "Same", "Text", DateTime.UtcNow.AddDays(-2));
        ListOnPageOne(new OrganizationData { Sid = "SAME", Name = "Same", MembersCount = 10 });

        await Create(NewDb()).DiscoverOrganizationsAsync();

        (await SnapshotsAsync("SAME")).Should().HaveCount(1);
        (await EventsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Phase1_AMemberCountDrift_IsNotAListingChange()
    {
        await SeedAsync("DRIFT", "Drift", "Text", DateTime.UtcNow.AddDays(-2));
        ListOnPageOne(new OrganizationData { Sid = "DRIFT", Name = "Drift", MembersCount = 9 });

        await Create(NewDb()).DiscoverOrganizationsAsync();

        (await SnapshotsAsync("DRIFT")).Should().HaveCount(1, "Phase 3 owns the member count (RSI's totalrows)");
        (await EventsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Phase1_AChangedListing_IsSnapshotted_KeepingTheKnownMemberCount()
    {
        await SeedAsync("RENAMED", "Old name", "Text", DateTime.UtcNow.AddDays(-2));
        ListOnPageOne(new OrganizationData { Sid = "RENAMED", Name = "New name", MembersCount = 7 });

        await Create(NewDb()).DiscoverOrganizationsAsync();

        var snapshots = await SnapshotsAsync("RENAMED");
        snapshots.Should().HaveCount(2);
        snapshots[^1].Name.Should().Be("New name");
        snapshots[^1].MembersCount.Should().Be(10);
        (await EventsAsync()).Select(e => e.ChangeType).Should().Equal("name_changed");
    }

    [Fact]
    public async Task Phase2_UnchangedContent_WritesNoSnapshot_ButRecordsTheCheck()
    {
        await SeedAsync("STILL", "Still", "Same text", DateTime.UtcNow.AddDays(-30));
        ServePage("STILL", "Same text");

        await Create(NewDb()).CollectOrganizationMetadataAsync();

        (await SnapshotsAsync("STILL")).Should().HaveCount(1);
        (await EventsAsync()).Should().BeEmpty();
        var checkedAt = await NewDb().DiscoveredOrganizations.Where(d => d.Sid == "STILL").Select(d => d.ContentCheckedAt).SingleAsync();
        checkedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Phase2_ComparesWithTheLastContentSnapshot_NotTheLatestListingOne()
    {
        await SeedAsync("TEXTS", "Texts", "Before", DateTime.UtcNow.AddDays(-30));
        var db = NewDb();
        db.Organizations.Add(new Organization
        {
            Sid = "TEXTS", Name = "Texts renamed", Timestamp = DateTime.UtcNow.AddDays(-1), MembersCount = 12,
            ContentCollected = false,
        });
        await db.SaveChangesAsync();
        ServePage("TEXTS", "After");

        await Create(NewDb()).CollectOrganizationMetadataAsync();

        var events = await EventsAsync();
        events.Select(e => (e.ChangeType, e.OldValue, e.NewValue)).Should().Equal(("description_changed", "Before", "After"));
        var latest = (await SnapshotsAsync("TEXTS"))[^1];
        latest.Should().Match<Organization>(o => o.ContentCollected && o.Description == "After"
            && o.Name == "Texts renamed" && o.MembersCount == 12);
    }

    [Fact]
    public async Task Phase2_TheFirstContentRead_IsNotAChange()
    {
        await SeedAsync("FRESH", "Fresh", description: null, DateTime.UtcNow.AddDays(-1));
        ServePage("FRESH", "First text");

        await Create(NewDb()).CollectOrganizationMetadataAsync();

        (await SnapshotsAsync("FRESH"))[^1].Description.Should().Be("First text");
        (await EventsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Phase2_RecentlyCheckedOrgs_AreNotRead()
    {
        await SeedAsync("CHECKED", "Checked", "Text", DateTime.UtcNow.AddDays(-30));
        var db = NewDb();
        (await db.DiscoveredOrganizations.SingleAsync(d => d.Sid == "CHECKED")).ContentCheckedAt = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        (await Create(NewDb()).CollectOrganizationMetadataAsync()).Should().Be(0);
        _rsi.Verify(r => r.GetOrgPageHtmlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
