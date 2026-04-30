using Collector.Models;

namespace Collector.Data.Repositories;

public interface IDiscoveredOrganizationRepository : IRepository<DiscoveredOrganization>
{
    Task<bool> ExistsAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllSidsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredOrganization>> GetAllUnprocessedAsync(CancellationToken ct = default);
    /// <summary>Returns orgs that have no organizations row with Timestamp >= since (not yet enriched this cycle), excluding tombstoned orgs.</summary>
    Task<IReadOnlyList<DiscoveredOrganization>> GetStaleAsync(DateTime since, CancellationToken ct = default);
    Task MarkProcessedAsync(string sid, CancellationToken ct = default);

    /// <summary>
    /// Atomically increments ConsecutiveNotFoundCount and sets DeadAt when the
    /// new count reaches <paramref name="deadThreshold"/>. Returns true if the
    /// org was tombstoned by this call.
    /// </summary>
    Task<bool> MarkNotFoundAsync(string sid, int deadThreshold, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Resets the not-found counter and clears DeadAt for an org whose page
    /// became reachable again.
    /// </summary>
    Task ResetNotFoundAsync(string sid, CancellationToken ct = default);
}