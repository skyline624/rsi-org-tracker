using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrganizationMemberRepository : IRepository<OrganizationMember>
{
    Task<IReadOnlyList<OrganizationMember>> GetByOrgSidAsync(string orgSid, DateTime? asOf = null, CancellationToken ct = default);

    /// <summary>The org's current members: one active row each, read through the (OrgSid, IsActive) index.</summary>
    Task<IReadOnlyList<OrganizationMember>> GetActiveByOrgSidAsync(string orgSid, CancellationToken ct = default);

    /// <summary>
    /// One page of an org's members (their latest row), by handle regardless of case:
    /// current members (<paramref name="active"/> true), former ones (false) or both (null).
    /// </summary>
    Task<(IReadOnlyList<OrganizationMember> Items, int Total)> GetLatestPageAsync(
        string orgSid, bool? active, int page, int pageSize, CancellationToken ct = default);
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