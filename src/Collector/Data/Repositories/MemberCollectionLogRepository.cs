using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class MemberCollectionLogRepository : Repository<MemberCollectionLog>, IMemberCollectionLogRepository
{
    public MemberCollectionLogRepository(TrackerDbContext context) : base(context) { }

    public async Task<MemberCollectionLog?> GetLatestAsync(string orgSid, CancellationToken ct = default)
    {
        return await DbSet
            .Where(l => l.OrgSid == orgSid)
            .OrderByDescending(l => l.CollectionTime)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<MemberCollectionLog>> GetByOrgSidAsync(string orgSid, int limit = 10, CancellationToken ct = default)
    {
        return await DbSet
            .Where(l => l.OrgSid == orgSid)
            .OrderByDescending(l => l.CollectionTime)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemberCollectionLog>> GetByCollectionTimeAsync(string orgSid, DateTime collectionTime, CancellationToken ct = default)
    {
        return await DbSet
            .Where(l => l.OrgSid == orgSid && l.CollectionTime == collectionTime)
            .ToListAsync(ct);
    }
}