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

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
