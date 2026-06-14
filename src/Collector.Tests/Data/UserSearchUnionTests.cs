using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// Guards the raw-SQL UNION behind <c>UsersController.SearchUsersAsync</c>: enriched
/// citizens (rows in <c>users</c>) plus roster-only members from
/// <c>organization_members</c> that never got a CitizenId — e.g. RSI accounts hiding
/// their citizen number, like the "Reclast" case. EF Core can't translate this UNION
/// under SQLite, so the controller ships raw SQL; this test executes the same SQL via
/// SqlQueryRaw to lock in the semantics. KEEP THE SQL HERE IN SYNC WITH THE CONTROLLER.
/// </summary>
public sealed class UserSearchUnionTests : IDisposable
{
    // Mirror of UsersController.SearchUsersAsync's UNION body. {0} = substring (enriched),
    // {1} = prefix (members).
    private const string Union = @"
        SELECT CitizenId, UserHandle, DisplayName, UrlImage, Bio, Location, Enlisted, UpdatedAt, 1 AS IsEnriched
        FROM users
        WHERE UserHandle LIKE {0} ESCAPE '\' OR (DisplayName IS NOT NULL AND DisplayName LIKE {0} ESCAPE '\')
        UNION ALL
        SELECT 0 AS CitizenId, m.UserHandle, m.DisplayName, m.UrlImage,
               NULL AS Bio, NULL AS Location, NULL AS Enlisted, m.Timestamp AS UpdatedAt, 0 AS IsEnriched
        FROM organization_members m
        WHERE m.UserHandle LIKE {1}
          AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = m.UserHandle)
          AND NOT EXISTS (SELECT 1 FROM organization_members o
                          WHERE o.UserHandle = m.UserHandle AND o.Timestamp > m.Timestamp)";

    private sealed class Row
    {
        public int CitizenId { get; set; }
        public string UserHandle { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? UrlImage { get; set; }
        public string? Bio { get; set; }
        public string? Location { get; set; }
        public DateTime? Enlisted { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsEnriched { get; set; }
    }

    private readonly SqliteConnection _connection;
    private readonly TrackerDbContext _db;

    public UserSearchUnionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new TrackerDbContext(options);
        _db.Database.EnsureCreated();

        // The NOCASE index the production search relies on (created by EnsureDatabaseAsync).
        _db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS IX_organization_members_UserHandle_NoCase
            ON organization_members (UserHandle COLLATE NOCASE);");
    }

    private async Task<Row[]> SearchAsync(string search)
    {
        var escaped = search.Replace("%", "\\%").Replace("_", "\\_");
        var substring = $"%{escaped}%";
        var prefixTerm = search.Trim().Replace("%", "");
        var prefix = prefixTerm.Length == 0 ? "" : prefixTerm + "%";

        var sql = $"SELECT * FROM ({Union}) ORDER BY UserHandle COLLATE NOCASE";
        return (await _db.Database.SqlQueryRaw<Row>(sql, substring, prefix).ToListAsync()).ToArray();
    }

    [Fact]
    public async Task Search_SurfacesRosterOnlyMemberWithoutCitizenId()
    {
        _db.Users.Add(new User { CitizenId = 111, UserHandle = "Verified", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        // Two snapshots of the same roster-only handle — the query must collapse to the latest.
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Reclast", CitizenId = null, DisplayName = "old", Timestamp = new DateTime(2026, 3, 1), IsActive = false },
            new OrganizationMember { OrgSid = "ORSU", UserHandle = "Reclast", CitizenId = null, DisplayName = "latest", Timestamp = new DateTime(2026, 6, 1), IsActive = true });
        await _db.SaveChangesAsync();

        var hits = await SearchAsync("Reclast");

        hits.Should().ContainSingle();
        hits[0].UserHandle.Should().Be("Reclast");
        hits[0].IsEnriched.Should().BeFalse();
        hits[0].CitizenId.Should().Be(0);
        hits[0].DisplayName.Should().Be("latest", "only the newest snapshot should survive");
    }

    [Fact]
    public async Task Search_IsCaseInsensitivePrefix()
    {
        _db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = "ORSU", UserHandle = "Reclast", CitizenId = null, Timestamp = new DateTime(2026, 6, 1), IsActive = true,
        });
        await _db.SaveChangesAsync();

        (await SearchAsync("recl")).Should().ContainSingle().Which.UserHandle.Should().Be("Reclast");
    }

    [Fact]
    public async Task Search_NonEnrichedIsPrefixOnly_NotSubstring()
    {
        // "clast" is a substring but NOT a prefix of "Reclast" — must not match the member side.
        _db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = "ORSU", UserHandle = "Reclast", CitizenId = null, Timestamp = new DateTime(2026, 6, 1), IsActive = true,
        });
        await _db.SaveChangesAsync();

        (await SearchAsync("clast")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_EnrichedHandleNotDuplicatedFromMembers()
    {
        // Same handle exists both as an enriched user and in organization_members.
        // It must appear once, flagged enriched — the member side excludes known users.
        _db.Users.Add(new User { CitizenId = 222, UserHandle = "Dexter", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = "ORSU", UserHandle = "Dexter", CitizenId = 222, Timestamp = new DateTime(2026, 6, 1), IsActive = true,
        });
        await _db.SaveChangesAsync();

        var hits = await SearchAsync("Dexter");

        hits.Should().ContainSingle();
        hits[0].IsEnriched.Should().BeTrue();
        hits[0].CitizenId.Should().Be(222);
    }

    [Fact]
    public async Task Search_EnrichedMatchesSubstring()
    {
        // Enriched side keeps substring semantics: "exte" matches inside "Dexter".
        _db.Users.Add(new User { CitizenId = 333, UserHandle = "Dexter", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        (await SearchAsync("exte")).Should().ContainSingle().Which.UserHandle.Should().Be("Dexter");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
