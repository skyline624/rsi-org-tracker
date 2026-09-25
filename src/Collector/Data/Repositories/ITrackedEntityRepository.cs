using Collector.Models;

namespace Collector.Data.Repositories;

public interface ITrackedEntityRepository : IRepository<TrackedEntity>
{
    Task<TrackedEntity?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default);
    Task<TrackedEntity?> GetByHandleAsync(string handle, CancellationToken ct = default);

    /// <summary>Several entities in one query, keyed by id (missing ids are absent).</summary>
    Task<Dictionary<long, TrackedEntity>> GetByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
}
