using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrganizationRepository : IRepository<Organization>
{
    Task<Organization?> GetLatestBySidAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<Organization>> GetHistoryBySidAsync(string sid, CancellationToken ct = default);
    Task<IReadOnlyList<Organization>> GetAllLatestAsync(CancellationToken ct = default);
    Task<Dictionary<string, Organization>> GetLatestBySidsAsync(IEnumerable<string> sids, CancellationToken ct = default);

    /// <summary>
    /// Updates the most-recent Organization snapshot's MembersCount to the authoritative
    /// value collected by Phase 3. Phase 1's MembersCount comes from the RSI search API
    /// which occasionally reports 0 for active orgs — Phase 3 is the ground truth.
    /// Returns the number of rows updated (0 if the latest snapshot already matches).
    /// </summary>
    Task<int> UpdateLatestMembersCountAsync(string sid, int membersCount, CancellationToken ct = default);
}