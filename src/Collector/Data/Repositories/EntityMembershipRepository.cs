using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class EntityMembershipRepository : Repository<EntityMembership>, IEntityMembershipRepository
{
    public EntityMembershipRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<EntityMembership>> GetByEntityIdAsync(long entityId, CancellationToken ct = default)
        => await DbSet.AsNoTracking()
            .Where(m => m.TrackedEntityId == entityId)
            .OrderByDescending(m => m.SinceDate)
            .ToListAsync(ct);

    // SIDs are stored in upper case: the requested SID (any capitalisation, e.g. from
    // an org page URL) is normalized and compared as-is, so the OrgSid indexes are used.
    public async Task<IReadOnlyList<EntityMembership>> GetByOrgSidAsync(string orgSid, CancellationToken ct = default)
    {
        var sid = orgSid.Trim().ToUpperInvariant();
        return await DbSet.AsNoTracking()
            .Where(m => m.OrgSid == sid)
            .OrderByDescending(m => m.SinceDate)
            .ToListAsync(ct);
    }

    public async Task<EntityMembership?> GetByEntityAndOrgAsync(long entityId, string orgSid, CancellationToken ct = default)
    {
        var sid = orgSid.Trim().ToUpperInvariant();
        return await DbSet.FirstOrDefaultAsync(m => m.TrackedEntityId == entityId && m.OrgSid == sid, ct);
    }

    public void Remove(EntityMembership membership) => DbSet.Remove(membership);
}
