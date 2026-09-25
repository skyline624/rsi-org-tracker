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

    /// <summary>What RSI answers for an org; by default one visible pilot.</summary>
    private Func<string, MemberCollectionResult> _roster = sid => Roster(RosterStatus.Complete, 1, $"{sid.ToLowerInvariant()}-pilot");

    private static MemberCollectionResult Roster(RosterStatus status, int totalRows, params string[] handles)
        => new(status,
            handles.Select(h => new MemberData { OrgSid = "", Handle = h, Rank = "Pilot" }).ToList(),
            totalRows, RawRows: handles.Length, RedactedRows: 0, HiddenRows: totalRows - handles.Length);

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
                var roster = _roster(sid);
                foreach (var member in roster.Members) member.OrgSid = sid;
                return roster;
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

    private async Task<List<string>> ActiveHandlesAsync(string sid)
    {
        var (_, db) = Create();
        return await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == sid && m.IsActive).Select(m => m.UserHandle).Distinct().OrderBy(h => h).ToListAsync();
    }

    private async Task<List<string>> EventTypesAsync()
    {
        var (_, db) = Create();
        return await db.ChangeEvents.AsNoTracking().Select(e => e.ChangeType).ToListAsync();
    }

    private async Task CollectAsync(string sid)
    {
        var (collector, _) = Create();
        await collector.CollectMembersForOrganizationAsync(sid);
    }

    [Theory]
    [InlineData(RosterStatus.Partial)]
    [InlineData(RosterStatus.Unreachable)]
    public async Task AnIncompleteRead_WritesNothing(RosterStatus status)
    {
        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "bravo");
        await CollectAsync("KEEP");
        var logRows = await LogRowCountAsync();

        _roster = _ => Roster(status, 2, "alpha");
        await CollectAsync("KEEP");

        (await ActiveHandlesAsync("KEEP")).Should().Equal("alpha", "bravo");
        (await EventTypesAsync()).Should().NotContain("member_left");
        (await LogRowCountAsync()).Should().Be(logRows);
    }

    [Fact]
    public async Task EveryRowMasked_DoesNotEmptyTheRoster()
    {
        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "bravo");
        await CollectAsync("MASKED");

        _roster = _ => Roster(RosterStatus.Complete, 2);
        await CollectAsync("MASKED");

        (await ActiveHandlesAsync("MASKED")).Should().Equal("alpha", "bravo");
        (await EventTypesAsync()).Should().NotContain("member_left");
    }

    [Fact]
    public async Task OrgGone_EmptiesTheRoster()
    {
        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "bravo");
        await CollectAsync("GONE");

        _roster = _ => MemberCollectionResult.Gone;
        await CollectAsync("GONE");

        (await ActiveHandlesAsync("GONE")).Should().BeEmpty();
    }

    [Fact]
    public async Task MembersCount_IsRsiTotalRows_MaskedRowsIncluded()
    {
        var (_, seed) = Create();
        seed.Organizations.Add(new Organization { Sid = "COUNT", Name = "Count", Timestamp = DateTime.UtcNow.AddDays(-1), MembersCount = 5 });
        await seed.SaveChangesAsync();
        _roster = _ => Roster(RosterStatus.Complete, 3, "alpha");

        await CollectAsync("COUNT");

        var (_, db) = Create();
        (await db.Organizations.AsNoTracking().Where(o => o.Sid == "COUNT").Select(o => o.MembersCount).SingleAsync())
            .Should().Be(3);
    }

    private async Task<int> LogRowCountAsync()
    {
        var (_, db) = Create();
        return await db.MemberCollectionLogs.CountAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
