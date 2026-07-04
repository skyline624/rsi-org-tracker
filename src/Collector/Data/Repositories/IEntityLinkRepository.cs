using Collector.Models;

namespace Collector.Data.Repositories;

public interface IEntityLinkRepository : IRepository<EntityLink>
{
    Task<IReadOnlyList<EntityLink>> GetByEntityAsync(long entityId, CancellationToken ct = default);
    Task<EntityLink?> GetByEntityProviderValueAsync(long entityId, string provider, string value, CancellationToken ct = default);
    void Remove(EntityLink link);
}
