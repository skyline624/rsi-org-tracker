using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrganizationMemberRepository : IRepository<OrganizationMember>
{
    Task<IReadOnlyList<OrganizationMember>> GetByOrgSidAsync(string orgSid, DateTime? asOf = null, CancellationToken ct = default);
    Task<IReadOnlyList<OrganizationMember>> GetByUserHandleAsync(string userHandle, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetKnownHandlesAsync(CancellationToken ct = default);
    Task<Dictionary<string, OrganizationMember>> GetLatestByOrgSidAsync(string orgSid, CancellationToken ct = default);
    Task UpdateCitizenIdByHandleAsync(string handle, int citizenId, CancellationToken ct = default);
}