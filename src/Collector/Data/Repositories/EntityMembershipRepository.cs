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

    public async Task<IReadOnlyList<EntityMembership>> GetByOrgSidAsync(string orgSid, CancellationToken ct = default)
        // Case-insensitive on the SID: the org page may be reached under any capitalisation.
        => await DbSet.AsNoTracking()
            .Where(m => m.OrgSid.ToLower() == orgSid.ToLower())
            .OrderByDescending(m => m.SinceDate)
            .ToListAsync(ct);

    public async Task<EntityMembership?> GetByEntityAndOrgAsync(long entityId, string orgSid, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(
            m => m.TrackedEntityId == entityId && m.OrgSid.ToLower() == orgSid.ToLower(), ct);

    public void Remove(EntityMembership membership) => DbSet.Remove(membership);
}
