using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class DiscoveredOrganizationRepository : Repository<DiscoveredOrganization>, IDiscoveredOrganizationRepository
{
    public DiscoveredOrganizationRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<string>> GetAllSidsAsync(CancellationToken ct = default)
    {
        return await DbSet
            .Select(d => d.Sid)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DiscoveredOrganization>> GetStaleAsync(DateTime since, CancellationToken ct = default)
    {
        // Orgs whose latest organizations row is older than `since` (or have none with ContentCollected=true).
        // Tombstoned orgs (DeadAt IS NOT NULL) are excluded — they 404'd consistently and aren't worth retrying.
        var freshSids = Context.Organizations
            .Where(o => o.Timestamp >= since && o.ContentCollected)
            .Select(o => o.Sid)
            .Distinct();

        return await DbSet
            .AsNoTracking()
            .Where(d => d.DeadAt == null && !freshSids.Contains(d.Sid))
            .ToListAsync(ct);
    }

    public async Task<bool> MarkNotFoundAsync(string sid, int deadThreshold, DateTime now, CancellationToken ct = default)
    {
        // Single atomic UPDATE so concurrent batches can't race past the threshold
        // (SQLite serializes writes, but expressing the threshold in SQL avoids a
        // load-then-store window that would be sloppy under any backend).
        var rows = await Context.Database.ExecuteSqlRawAsync(
            @"UPDATE discovered_organizations
              SET ConsecutiveNotFoundCount = ConsecutiveNotFoundCount + 1,
                  DeadAt = CASE
                      WHEN ConsecutiveNotFoundCount + 1 >= {0} AND DeadAt IS NULL
                          THEN {1}
                      ELSE DeadAt
                  END
              WHERE Sid = {2};",
            new object[] { deadThreshold, now.ToString("o"), sid },
            ct);

        if (rows == 0) return false;

        // Cheap follow-up read just to tell the caller whether THIS call tombstoned.
        var entity = await DbSet.AsNoTracking().FirstOrDefaultAsync(d => d.Sid == sid, ct);
        return entity?.DeadAt != null && entity.ConsecutiveNotFoundCount >= deadThreshold;
    }

    public async Task ResetNotFoundAsync(string sid, CancellationToken ct = default)
    {
        await Context.Database.ExecuteSqlRawAsync(
            @"UPDATE discovered_organizations
              SET ConsecutiveNotFoundCount = 0, DeadAt = NULL
              WHERE Sid = {0} AND (ConsecutiveNotFoundCount > 0 OR DeadAt IS NOT NULL);",
            new object[] { sid },
            ct);
    }

    public async Task<IReadOnlyList<string>> GetMemberCollectionTargetsAsync(CancellationToken ct = default)
        => await DbSet.AsNoTracking()
            .Where(o => o.DeadAt == null)
            .OrderBy(o => o.LastMembersCollectedAt == null ? 0 : 1)
            .ThenBy(o => o.LastMembersCollectedAt)
            .ThenBy(o => o.Sid)
            .Select(o => o.Sid)
            .ToListAsync(ct);

    public Task MarkMembersCollectedAsync(string sid, DateTime when, CancellationToken ct = default)
        => DbSet.Where(o => o.Sid == sid)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LastMembersCollectedAt, when), ct);
}
