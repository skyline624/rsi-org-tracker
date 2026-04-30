using Collector.Models;

namespace Collector.Data.Repositories;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default);
    Task<User?> GetByHandleAsync(string handle, CancellationToken ct = default);
    Task<bool> ExistsAsync(int citizenId, CancellationToken ct = default);
    Task<Dictionary<string, string?>> GetDisplayNamesByHandlesAsync(IReadOnlyList<string> handles, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetExistingHandlesAsync(IReadOnlyList<string> handles, CancellationToken ct = default);
}