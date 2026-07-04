using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class EntityNoteRepository : Repository<EntityNote>, IEntityNoteRepository
{
    public EntityNoteRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<EntityNote>> GetByEntityIdAsync(long entityId, CancellationToken ct = default)
        => await DbSet.AsNoTracking()
            .Where(n => n.TrackedEntityId == entityId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);

    public void Remove(EntityNote note) => DbSet.Remove(note);
}
