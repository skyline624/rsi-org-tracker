using Collector.Models;

namespace Collector.Data.Repositories;

public interface IEntityAudioRepository : IRepository<EntityAudio>
{
    Task<IReadOnlyList<EntityAudio>> GetByEntityIdAsync(long entityId, CancellationToken ct = default);
    void Remove(EntityAudio audio);
}
