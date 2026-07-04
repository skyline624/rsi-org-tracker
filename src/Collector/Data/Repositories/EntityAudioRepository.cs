using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class EntityAudioRepository : Repository<EntityAudio>, IEntityAudioRepository
{
    public EntityAudioRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<EntityAudio>> GetByEntityIdAsync(long entityId, CancellationToken ct = default)
        => await DbSet.AsNoTracking()
            .Where(a => a.TrackedEntityId == entityId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    public void Remove(EntityAudio audio) => DbSet.Remove(audio);
}
