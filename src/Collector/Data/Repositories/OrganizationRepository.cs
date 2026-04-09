using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrganizationRepository : Repository<Organization>, IOrganizationRepository
{
    public OrganizationRepository(TrackerDbContext context) : base(context) { }

    public async Task<Organization?> GetLatestBySidAsync(string sid, CancellationToken ct = default)
    {
        return await DbSet
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Organization>> GetHistoryBySidAsync(string sid, CancellationToken ct = default)
    {
        return await DbSet
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Organization>> GetAllLatestAsync(CancellationToken ct = default)
    {
        // AsNoTracking — this projection is read-only, we never mutate the results.
        var latestQuery = DbSet
            .AsNoTracking()
            .GroupBy(o => o.Sid)
            .Select(g => g.OrderByDescending(o => o.Timestamp).First());

        return await latestQuery.ToListAsync(ct);
    }

    public async Task<Dictionary<string, Organization>> GetLatestBySidsAsync(IEnumerable<string> sids, CancellationToken ct = default)
    {
        var sidList = sids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var orgs = await DbSet
            .AsNoTracking()
            .Where(o => sidList.Contains(o.Sid))
            .GroupBy(o => o.Sid)
            .Select(g => g.OrderByDescending(o => o.Timestamp).First())
            .ToListAsync(ct);

        return orgs.ToDictionary(o => o.Sid, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> UpdateLatestMembersCountAsync(
        string sid, int membersCount, CancellationToken ct = default)
    {
        // ExecuteUpdateAsync on the latest snapshot only — bypasses change tracking
        // so we don't touch any entity already loaded in the scoped DbContext.
        var latestTimestamp = await DbSet
            .AsNoTracking()
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .Select(o => (DateTime?)o.Timestamp)
            .FirstOrDefaultAsync(ct);

        if (latestTimestamp is null) return 0;

        return await DbSet
            .Where(o => o.Sid == sid && o.Timestamp == latestTimestamp && o.MembersCount != membersCount)
            .ExecuteUpdateAsync(setter => setter.SetProperty(o => o.MembersCount, membersCount), ct);
    }
}