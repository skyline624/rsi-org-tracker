using Collector.Data;
using Collector.Data.Repositories;
using Collector.Extensions;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>Collector reads that only look at rows: nothing may stay tracked in the DbContext.</summary>
public sealed class MemberReadsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private static readonly DateTime Collected = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());

        var db = NewDb();
        foreach (var (org, day) in new[] { ("ALPHA", 1), ("ALPHA", 2), ("BETA", 2) })
        {
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrgSid = org, UserHandle = "pilot", Timestamp = Collected.AddDays(day),
            });
        }
        db.MemberCollectionLogs.Add(new MemberCollectionLog { OrgSid = "ALPHA", CollectionTime = Collected, UserHandle = "pilot" });
        await db.SaveChangesAsync();
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    [Fact]
    public async Task OrgSidsForHandle_AreDistinct_AndNothingIsTracked()
    {
        var db = NewDb();

        var sids = await new OrganizationMemberRepository(db).GetOrgSidsForHandleAsync("pilot", activeOnly: false);

        sids.Should().BeEquivalentTo(["ALPHA", "BETA"]);
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task MemberCollectionLogReads_TrackNothing()
    {
        var db = NewDb();
        var logs = new MemberCollectionLogRepository(db);

        var latest = await logs.GetLatestAsync("ALPHA");
        var rows = await logs.GetByCollectionTimeAsync("ALPHA", latest!.CollectionTime);
        await logs.GetByOrgSidAsync("ALPHA");

        rows.Should().ContainSingle();
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task ARosterAtAPastTime_IsPagedByTheDatabase_EachMemberByItsLatestRowThen()
    {
        // at_time used to load every row of the org and page in memory (TEST: 442 k rows).
        var db = NewDb();
        var t0 = Collected.AddDays(10);
        foreach (var (handle, days, rank) in new[]
                 {
                     ("charlie", 0, "Recruit"), ("charlie", 2, "Member"), ("charlie", 5, "Officer"),
                     ("Bravo", 1, "Pilot"), ("alpha", 3, "Pilot"), ("delta", 6, "Pilot"),
                 })
        {
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "GAMMA", UserHandle = handle, Rank = rank, Timestamp = t0.AddDays(days) });
        }
        await db.SaveChangesAsync();
        var repo = new OrganizationMemberRepository(NewDb());

        var (page1, total) = await repo.GetPageAtAsync("GAMMA", t0.AddDays(4), page: 1, pageSize: 2);
        var (page2, _) = await repo.GetPageAtAsync("GAMMA", t0.AddDays(4), page: 2, pageSize: 2);

        total.Should().Be(3, "delta only appears after that time");
        page1.Select(m => m.UserHandle).Should().Equal("alpha", "Bravo");
        page2.Select(m => (m.UserHandle, m.Rank)).Should().Equal(("charlie", "Member"));
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
