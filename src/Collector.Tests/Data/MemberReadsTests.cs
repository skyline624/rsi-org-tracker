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

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
