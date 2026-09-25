using Collector.Data;
using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Extensions;
using Collector.Models;
using Collector.Options;
using Collector.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>Phase 3 against a real (in-memory) tracker schema, with RSI mocked.</summary>
public sealed class MemberCollectorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IRsiApiClient> _rsi = new();
    private ServiceProvider _provider = null!;
    private readonly List<string> _requestedSids = [];

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());

        _rsi.Setup(r => r.GetAllOrganizationMembersAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sid, int _, CancellationToken _) =>
            {
                _requestedSids.Add(sid);
                return new MemberCollectionResult(
                    [new MemberData { OrgSid = sid, Handle = $"{sid.ToLowerInvariant()}-pilot", Rank = "Pilot" }],
                    OrgExists: true, Reachable: true);
            });
    }

    private (MemberCollector Collector, TrackerDbContext Db) Create()
    {
        var db = _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();
        var collector = new MemberCollector(
            _rsi.Object,
            new OrganizationRepository(db),
            new OrganizationMemberRepository(db),
            new MemberCollectionLogRepository(db),
            new ChangeEventRepository(db),
            new UserEnrichmentQueueRepository(db),
            new DiscoveredOrganizationRepository(db),
            new ChangeDetector(NullLogger<ChangeDetector>.Instance),
            new UserRepository(db),
            NullLogger<MemberCollector>.Instance,
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions()));
        return (collector, db);
    }

    private async Task DiscoverAsync(string sid, DateTime? lastCollected = null, DateTime? deadAt = null)
    {
        var (_, db) = Create();
        db.DiscoveredOrganizations.Add(new DiscoveredOrganization
        {
            Sid = sid, Name = sid, DiscoveredAt = DateTime.UtcNow.AddDays(-30),
            LastMembersCollectedAt = lastCollected, DeadAt = deadAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CollectingAnOrganization_LeavesNothingTrackedInTheDbContext()
    {
        await DiscoverAsync("TRACKED");
        var (collector, db) = Create();

        await collector.CollectMembersForOrganizationAsync("TRACKED");

        db.ChangeTracker.Entries().Should().BeEmpty("a DbContext that keeps every inserted row grows for ever");
    }

    [Fact]
    public async Task Phase3_VisitsLiveDiscoveredOrganizations_LeastRecentlyCollectedFirst()
    {
        var now = DateTime.UtcNow;
        await DiscoverAsync("RECENT", lastCollected: now.AddHours(-1));
        await DiscoverAsync("NEVER");
        await DiscoverAsync("OLDER", lastCollected: now.AddDays(-3));
        await DiscoverAsync("DEAD", deadAt: now.AddDays(-1));
        var (collector, _) = Create();

        await collector.CollectAllMembersAsync();

        _requestedSids.Should().Equal("NEVER", "OLDER", "RECENT");
    }

    [Fact]
    public async Task Phase3_RecordsWhenEachOrganizationWasCollected_SoAnInterruptedCycleResumes()
    {
        await DiscoverAsync("STAMPED");
        var before = DateTime.UtcNow;
        var (collector, _) = Create();

        await collector.CollectAllMembersAsync();

        var (_, db) = Create();
        var org = await db.DiscoveredOrganizations.AsNoTracking().SingleAsync(o => o.Sid == "STAMPED");
        org.LastMembersCollectedAt.Should().NotBeNull().And.BeOnOrAfter(before.AddSeconds(-1));
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
