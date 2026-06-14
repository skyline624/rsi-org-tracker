using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// Locks in the "member since" computation behind UsersController.GetOrganizations:
/// the earliest snapshot per (handle, org). Mirrors the controller's GroupBy + Min query.
/// </summary>
public sealed class MemberSinceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TrackerDbContext _db;

    public MemberSinceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    private Task<Dictionary<string, DateTime>> FirstSeenAsync(string handle) =>
        _db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => new { OrgSid = g.Key, First = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.OrgSid, x => x.First);

    [Fact]
    public async Task FirstSeen_IsEarliestSnapshotPerOrg()
    {
        _db.OrganizationMembers.AddRange(
            // ORSU: three snapshots — earliest is 2026-03-23.
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Reclast", Timestamp = new DateTime(2026, 4, 15), IsActive = true },
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Reclast", Timestamp = new DateTime(2026, 3, 23), IsActive = true },
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Reclast", Timestamp = new DateTime(2026, 6, 14), IsActive = true },
            // MANDOKUIL: single snapshot.
            new OrganizationMember { OrgSid = "MANDOKUIL", UserHandle = "Reclast", Timestamp = new DateTime(2026, 2, 22), IsActive = false });
        await _db.SaveChangesAsync();

        var firstSeen = await FirstSeenAsync("Reclast");

        firstSeen["ORSU"].Should().Be(new DateTime(2026, 3, 23));
        firstSeen["MANDOKUIL"].Should().Be(new DateTime(2026, 2, 22));
    }

    [Fact]
    public async Task FirstSeen_DoesNotLeakAcrossHandles()
    {
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Reclast", Timestamp = new DateTime(2026, 3, 23), IsActive = true },
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Someone", Timestamp = new DateTime(2026, 1, 1), IsActive = true });
        await _db.SaveChangesAsync();

        (await FirstSeenAsync("Reclast"))["ORSU"].Should().Be(new DateTime(2026, 3, 23));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
