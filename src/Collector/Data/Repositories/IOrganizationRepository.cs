using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrganizationRepository : IRepository<Organization>
{
    Task<Organization?> GetLatestBySidAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<Organization>> GetHistoryBySidAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<Organization>> GetAllLatestAsync(CancellationToken ct = default);
    Task<Dictionary<string, Organization>> GetLatestBySidsAsync(IEnumerable<string> sids, CancellationToken ct = default);
}