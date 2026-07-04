using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class EntityLinkRepository : Repository<EntityLink>, IEntityLinkRepository
{
    public EntityLinkRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<EntityLink>> GetByEntityAsync(long entityId, CancellationToken ct = default)
        => await DbSet.AsNoTracking()
            .Where(l => l.TrackedEntityId == entityId)
            .OrderBy(l => l.Provider)
            .ToListAsync(ct);

    public async Task<EntityLink?> GetByEntityProviderValueAsync(long entityId, string provider, string value, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(
            l => l.TrackedEntityId == entityId && l.Provider == provider && l.Value == value, ct);

    public void Remove(EntityLink link) => DbSet.Remove(link);
}
