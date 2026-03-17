using Collector.Data;
using Collector.Api.Dtos.Stats;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

public class StatsService
{
    private readonly TrackerDbContext _db;

    public StatsService(TrackerDbContext db)
    {
        _db = db;
    }

    public async Task<StatsOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var orgCount = await _db.Organizations.Select(o => o.Sid).Distinct().CountAsync(ct);
        var userCount = await _db.Users.CountAsync(ct);
        var lastCollectionAt = await _db.Organizations.MaxAsync(o => (DateTime?)o.Timestamp, ct);

        return new StatsOverviewDto
        {
            TotalOrganizations = orgCount,
            TotalUsers = userCount,
            LastCollectionAt = lastCollectionAt,
        };
    }

    public async Task<IReadOnlyList<TimelinePointDto>> GetTimelineAsync(int days = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        return await _db.ChangeEvents
            .Where(e => e.Timestamp >= since)
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new TimelinePointDto { Date = g.Key.ToString(), ChangeCount = g.Count() })
            .OrderBy(p => p.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrganizationTopDto>> GetTopOrganizationsAsync(int limit = 10, CancellationToken ct = default)
    {
        // Raw SQL: avoids materializing 103k Organization objects
        var sql = $"""
            SELECT o.Sid, o.Name, o.MembersCount, o.Archetype
            FROM organizations AS o
            INNER JOIN (
                SELECT Sid, MAX(Timestamp) AS MaxTs
                FROM organizations GROUP BY Sid
            ) AS g ON o.Sid = g.Sid AND o.Timestamp = g.MaxTs
            ORDER BY o.MembersCount DESC
            LIMIT {limit}
            """;

        var results = new List<OrganizationTopDto>();
        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new OrganizationTopDto
                {
                    Sid = reader.GetString(0),
                    Name = reader.GetString(1),
                    MembersCount = reader.GetInt32(2),
                    Archetype = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
        return results;
    }

    public async Task<IReadOnlyList<ArchetypeStatsDto>> GetArchetypesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(o.Archetype, 'Unknown') AS Archetype, COUNT(*) AS Count
            FROM organizations AS o
            INNER JOIN (
                SELECT Sid, MAX(Timestamp) AS MaxTs
                FROM organizations GROUP BY Sid
            ) AS g ON o.Sid = g.Sid AND o.Timestamp = g.MaxTs
            GROUP BY COALESCE(o.Archetype, 'Unknown')
            ORDER BY Count DESC
            """;

        var results = new List<ArchetypeStatsDto>();
        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new ArchetypeStatsDto
                {
                    Archetype = reader.GetString(0),
                    Count = reader.GetInt32(1),
                });
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
        return results;
    }

    public async Task<IReadOnlyList<MemberActivityDto>> GetMemberActivityAsync(int days = 30, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var joins = await _db.ChangeEvents
            .Where(e => e.Timestamp >= since && e.ChangeType == "member_joined")
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var leaves = await _db.ChangeEvents
            .Where(e => e.Timestamp >= since && e.ChangeType == "member_left")
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var allDates = joins.Select(j => j.Date).Union(leaves.Select(l => l.Date)).OrderBy(d => d);
        var joinsDict = joins.ToDictionary(j => j.Date, j => j.Count);
        var leavesDict = leaves.ToDictionary(l => l.Date, l => l.Count);

        return allDates.Select(d => new MemberActivityDto
        {
            Date = d.ToString("yyyy-MM-dd"),
            Joins = joinsDict.GetValueOrDefault(d),
            Leaves = leavesDict.GetValueOrDefault(d),
            Total = joinsDict.GetValueOrDefault(d) + leavesDict.GetValueOrDefault(d),
        }).ToList();
    }
}
