using Collector.Models;

namespace Collector.Data.Repositories;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default);
    Task<User?> GetByHandleAsync(string handle, CancellationToken ct = default);
    /// <summary>
    /// Citizen numbers of the given handles: current handles first (users), then former
    /// ones (user_handle_history). Keys compare case-insensitively; the lookup itself is
    /// exact-case so it can use the UserHandle indexes.
    /// </summary>
    Task<Dictionary<string, int>> GetCitizenIdsByHandlesAsync(IReadOnlyCollection<string> handles, CancellationToken ct = default);

    /// <summary>
    /// Up to <paramref name="limit"/> citizens after <paramref name="afterId"/>, in Id order,
    /// whose profile was last read by a parser older than <paramref name="version"/>.
    /// </summary>
    Task<IReadOnlyList<ProfileToRefresh>> GetProfilesToRefreshAsync(long afterId, int version, int limit, CancellationToken ct = default);

    /// <summary>
    /// The profile was read by <paramref name="version"/> and had nothing to store (404,
    /// no citizen record, handle now held by another citizen): it is not read again.
    /// </summary>
    Task MarkProfileReadAsync(long id, int version, CancellationToken ct = default);
}

/// <summary>A citizen whose profile Phase 4 reads again.</summary>
public sealed record ProfileToRefresh(long Id, int CitizenId, string UserHandle);