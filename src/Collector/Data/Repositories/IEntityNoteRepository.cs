using Collector.Models;

namespace Collector.Data.Repositories;

public interface IEntityNoteRepository : IRepository<EntityNote>
{
    Task<IReadOnlyList<EntityNote>> GetByEntityIdAsync(long entityId, CancellationToken ct = default);
    void Remove(EntityNote note);
}
