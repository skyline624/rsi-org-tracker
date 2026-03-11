using Collector.Models;

namespace Collector.Data.Repositories;

public interface IDiscoveredOrganizationRepository : IRepository<DiscoveredOrganization>
{
    Task<bool> ExistsAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredOrganization>> GetAllUnprocessedAsync(CancellationToken ct = default);
    Task MarkProcessedAsync(string sid, CancellationToken ct = default);
}