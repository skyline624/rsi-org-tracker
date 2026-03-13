using Collector.Models;

namespace Collector.Data.Repositories;

public interface IDiscoveredOrganizationRepository : IRepository<DiscoveredOrganization>
{
    Task<bool> ExistsAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllSidsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredOrganization>> GetAllUnprocessedAsync(CancellationToken ct = default);
    /// <summary>Returns orgs that have no organizations row with Timestamp >= since (not yet enriched this cycle).</summary>
    Task<IReadOnlyList<DiscoveredOrganization>> GetStaleAsync(DateTime since, CancellationToken ct = default);
    Task MarkProcessedAsync(string sid, CancellationToken ct = default);
}