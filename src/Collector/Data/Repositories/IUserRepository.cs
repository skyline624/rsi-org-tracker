using Collector.Models;

namespace Collector.Data.Repositories;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default);
    Task<User?> GetByHandleAsync(string handle, CancellationToken ct = default);
    Task<Dictionary<string, string?>> GetDisplayNamesByHandlesAsync(IReadOnlyList<string> handles, CancellationToken ct = default);

    /// <summary>
    /// Citizen numbers of the given handles: current handles first (users), then former
    /// ones (user_handle_history). Keys compare case-insensitively; the lookup itself is
    /// exact-case so it can use the UserHandle indexes.
    /// </summary>
    Task<Dictionary<string, int>> GetCitizenIdsByHandlesAsync(IReadOnlyCollection<string> handles, CancellationToken ct = default);
}