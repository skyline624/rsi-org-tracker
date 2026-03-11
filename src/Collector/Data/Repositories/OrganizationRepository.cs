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
        // Get the latest snapshot for each organization
        var latestQuery = DbSet
            .GroupBy(o => o.Sid)
            .Select(g => g.OrderByDescending(o => o.Timestamp).First());

        return await latestQuery.ToListAsync(ct);
    }

    public async Task<Dictionary<string, Organization>> GetLatestBySidsAsync(IEnumerable<string> sids, CancellationToken ct = default)
    {
        var sidList = sids.ToList();
        var orgs = await DbSet
            .Where(o => sidList.Contains(o.Sid))
            .GroupBy(o => o.Sid)
            .Select(g => g.OrderByDescending(o => o.Timestamp).First())
            .ToListAsync(ct);

        return orgs.ToDictionary(o => o.Sid);
    }
}