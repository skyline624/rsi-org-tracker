using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrganizationMemberRepository : IRepository<OrganizationMember>
{
    Task<IReadOnlyList<OrganizationMember>> GetByOrgSidAsync(string orgSid, DateTime? asOf = null, CancellationToken ct = default);
    /// <summary>Distinct organizations a handle appears in: currently, or ever when <paramref name="activeOnly"/> is false.</summary>
    Task<IReadOnlyList<string>> GetOrgSidsForHandleAsync(string userHandle, bool activeOnly, CancellationToken ct = default);
    Task UpdateCitizenIdByHandleAsync(string handle, int citizenId, CancellationToken ct = default);
    /// <summary>
    /// Marks every active row of the org whose <see cref="OrganizationMember.Timestamp"/>
    /// is strictly before <paramref name="currentTimestamp"/> as inactive.
    /// Returns the number of rows affected.
    /// </summary>
    Task<int> MarkAllPreviousInactiveAsync(string orgSid, DateTime currentTimestamp, CancellationToken ct = default);
}