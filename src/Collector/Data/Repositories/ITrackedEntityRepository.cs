using Collector.Models;

namespace Collector.Data.Repositories;

public interface ITrackedEntityRepository : IRepository<TrackedEntity>
{
    Task<TrackedEntity?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default);
    Task<TrackedEntity?> GetByHandleAsync(string handle, CancellationToken ct = default);
}
