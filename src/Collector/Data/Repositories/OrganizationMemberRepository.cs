using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrganizationMemberRepository : Repository<OrganizationMember>, IOrganizationMemberRepository
{
    public OrganizationMemberRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<OrganizationMember>> GetByOrgSidAsync(string orgSid, DateTime? asOf = null, CancellationToken ct = default)
    {
        var query = DbSet.Where(m => m.OrgSid == orgSid);

        if (asOf.HasValue)
        {
            query = query.Where(m => m.Timestamp <= asOf.Value);
        }

        return await query
            .GroupBy(m => m.UserHandle)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrganizationMember>> GetByUserHandleAsync(string userHandle, CancellationToken ct = default)
    {
        return await DbSet
            .Where(m => m.UserHandle == userHandle)
            .OrderByDescending(m => m.Timestamp)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetKnownHandlesAsync(CancellationToken ct = default)
    {
        return await DbSet
            .Select(m => m.UserHandle)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<Dictionary<string, OrganizationMember>> GetLatestByOrgSidAsync(string orgSid, CancellationToken ct = default)
    {
        var members = await DbSet
            .Where(m => m.OrgSid == orgSid)
            .GroupBy(m => m.UserHandle)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToListAsync(ct);

        return members.ToDictionary(m => m.UserHandle);
    }

    public async Task UpdateCitizenIdByHandleAsync(string handle, int citizenId, CancellationToken ct = default)
    {
        await DbSet
            .Where(m => m.UserHandle == handle && m.CitizenId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CitizenId, citizenId), ct);
    }
}