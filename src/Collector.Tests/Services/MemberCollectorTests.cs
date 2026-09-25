using Collector.Data;
using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Extensions;
using Collector.Models;
using Collector.Options;
using Collector.Parsers;
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
            handles.Select(h => new MemberData { OrgSid = "", Handle = h, Rank = "Pilot", Stars = 3 }).ToList(),
            totalRows, RawRows: totalRows, RedactedRows: 0, HiddenRows: totalRows - handles.Length);

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
            new OrgMemberCountRepository(db),
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

    [Fact]
    public async Task Capped_KeepsMembersBeyondRsisWindow_AndInfersNoDeparture()
    {
        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "bravo");
        await CollectAsync("HUGE");

        _roster = _ => Roster(RosterStatus.Capped, 20_000, "alpha", "charlie");
        await CollectAsync("HUGE");

        (await ActiveHandlesAsync("HUGE")).Should().Equal("alpha", "bravo", "charlie");
        (await EventTypesAsync()).Should().NotContain("member_left");
    }

    [Fact]
    public async Task Stars_AreStoredWithTheMember()
    {
        await CollectAsync("STARS");

        var (_, db) = Create();
        (await db.OrganizationMembers.AsNoTracking().Where(m => m.OrgSid == "STARS").Select(m => m.Stars).SingleAsync())
            .Should().Be(3);
    }

    [Fact]
    public async Task Counters_AreRecordedOnlyWhenOneChanges()
    {
        _roster = _ => Roster(RosterStatus.Complete, 3, "alpha");
        await CollectAsync("COUNTS");
        await CollectAsync("COUNTS");
        _roster = _ => Roster(RosterStatus.Complete, 4, "alpha", "bravo");
        await CollectAsync("COUNTS");

        var rows = await CountersAsync("COUNTS");
        rows.Should().Equal((3, 1, 0, 2), (4, 2, 0, 2));
    }

    [Fact]
    public async Task Counters_OfAnIncompleteRead_KeepOnlyTheTotal()
    {
        _roster = _ => Roster(RosterStatus.Partial, 5, "alpha");
        await CollectAsync("PARTIAL");
        await CollectAsync("PARTIAL");

        (await CountersAsync("PARTIAL")).Should().Equal((5, (int?)null, (int?)null, (int?)null));
    }

    [Fact]
    public async Task AKnownCitizen_NewToTheOrg_IsAnnouncedInPhase3()
    {
        await SeedUserAsync("charlie", 777);
        _roster = _ => Roster(RosterStatus.Complete, 1, "alpha");
        await CollectAsync("JOIN");

        _roster = _ => Roster(RosterStatus.Complete, 3, "alpha", "charlie", "stranger");
        await CollectAsync("JOIN");

        var (_, db) = Create();
        var joined = await db.ChangeEvents.AsNoTracking()
            .Where(e => e.ChangeType == "member_joined").Select(e => e.UserHandle).ToListAsync();
        joined.Should().Equal(["charlie"], "an unknown handle is announced by Phase 4 once its profile is read");
    }

    [Fact]
    public async Task ARenamedMember_IsNeitherLeftNorJoined()
    {
        await SeedUserAsync("oldname", 555);
        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "oldname");
        await CollectAsync("RENAME");

        var (_, db) = Create();
        var user = await db.Users.SingleAsync(u => u.CitizenId == 555);
        user.UserHandle = "newname";
        db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = 555, UserHandle = "oldname", FirstSeen = DateTime.UtcNow.AddDays(-9), LastSeen = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "newname");
        await CollectAsync("RENAME");

        (await EventTypesAsync()).Should().NotContain(["member_left", "member_joined"]);
    }

    [Fact]
    public async Task TheFirstCollectionAfterV1Rows_IsABaselineWithoutMemberEvents()
    {
        var (_, db) = Create();
        var before = DateTime.UtcNow.AddDays(-1);
        foreach (var handle in new[] { "alpha", "bravo" })
        {
            db.MemberCollectionLogs.Add(new MemberCollectionLog
            {
                OrgSid = "LEGACY", CollectionTime = before, UserHandle = handle, Rank = "Roles", ParserVersion = 1,
            });
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrgSid = "LEGACY", UserHandle = handle, Rank = "Roles", Timestamp = before,
            });
        }
        await db.SaveChangesAsync();
        await SeedUserAsync("charlie", 888);

        _roster = _ => Roster(RosterStatus.Complete, 2, "alpha", "charlie");
        await CollectAsync("LEGACY");

        (await EventTypesAsync()).Should().BeEmpty("v1 ranks were overlay titles: comparing them would be noise");
        var (_, check) = Create();
        (await check.MemberCollectionLogs.AsNoTracking().Where(l => l.OrgSid == "LEGACY" && l.CollectionTime > before)
            .Select(l => l.ParserVersion).Distinct().ToListAsync()).Should().Equal(MemberHtmlParser.Version);
    }

    private async Task SeedUserAsync(string handle, int citizenId)
    {
        var (_, db) = Create();
        db.Users.Add(new User
        {
            CitizenId = citizenId, UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<(int, int?, int?, int?)>> CountersAsync(string sid)
    {
        var (_, db) = Create();
        return (await db.OrgMemberCounts.AsNoTracking().Where(c => c.OrgSid == sid).OrderBy(c => c.Id).ToListAsync())
            .Select(c => (c.TotalRows, c.VisibleCount, c.RedactedCount, c.HiddenCount)).ToList();
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
