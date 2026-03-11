using Collector.Models;

namespace Collector.Data.Repositories;

public interface IUserHandleHistoryRepository : IRepository<UserHandleHistory>
{
    Task<IReadOnlyList<UserHandleHistory>> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default);
    Task<UserHandleHistory?> GetLatestAsync(int citizenId, CancellationToken ct = default);
    Task<UserHandleHistory?> GetByHandleAsync(string handle, CancellationToken ct = default);
}