using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrganizationMemberRepository : Repository<OrganizationMember>, IOrganizationMemberRepository
{
    public OrganizationMemberRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<OrganizationMember>> GetByOrgSidAsync(string orgSid, DateTime? asOf = null, CancellationToken ct = default)
    {
        // EF Core on SQLite translates `GroupBy().Select(g => g.OrderByDescending().First())`
        // into a correlated subquery that does ~O(N²) work. For TEST Squadron
        // (~15k rows after GroupBy, ~27k raw rows) the LINQ version takes ~2 minutes;
        // the window-function variant below takes ~70 ms — a 1700x speedup.
        if (asOf.HasValue)
        {
            var cutoff = asOf.Value;
            return await DbSet
                .FromSqlInterpolated($@"
                    SELECT Id, OrgSid, UserHandle, CitizenId, Timestamp, DisplayName,
                           Rank, RolesJson, UrlImage, IsActive
                    FROM (
                        SELECT *,
                               ROW_NUMBER() OVER (PARTITION BY UserHandle ORDER BY Timestamp DESC) AS _rn
                        FROM organization_members
                        WHERE OrgSid = {orgSid}
                          AND Timestamp <= {cutoff}
                    )
                    WHERE _rn = 1")
                .AsNoTracking()
                .ToListAsync(ct);
        }

        return await DbSet
            .FromSqlInterpolated($@"
                SELECT Id, OrgSid, UserHandle, CitizenId, Timestamp, DisplayName,
                       Rank, RolesJson, UrlImage, IsActive
                FROM (
                    SELECT *,
                           ROW_NUMBER() OVER (PARTITION BY UserHandle ORDER BY Timestamp DESC) AS _rn
                    FROM organization_members
                    WHERE OrgSid = {orgSid}
                )
                WHERE _rn = 1")
            .AsNoTracking()
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
        // Same window-function trick as GetByOrgSidAsync — see its comment.
        var members = await DbSet
            .FromSqlInterpolated($@"
                SELECT Id, OrgSid, UserHandle, CitizenId, Timestamp, DisplayName,
                       Rank, RolesJson, UrlImage, IsActive
                FROM (
                    SELECT *,
                           ROW_NUMBER() OVER (PARTITION BY UserHandle ORDER BY Timestamp DESC) AS _rn
                    FROM organization_members
                    WHERE OrgSid = {orgSid}
                )
                WHERE _rn = 1")
            .AsNoTracking()
            .ToListAsync(ct);

        return members.ToDictionary(m => m.UserHandle);
    }

    public async Task UpdateCitizenIdByHandleAsync(string handle, int citizenId, CancellationToken ct = default)
    {
        await DbSet
            .Where(m => m.UserHandle == handle && m.CitizenId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CitizenId, citizenId), ct);
    }

    public async Task<int> MarkAllPreviousInactiveAsync(string orgSid, DateTime currentTimestamp, CancellationToken ct = default)
    {
        return await DbSet
            .Where(m => m.OrgSid == orgSid && m.Timestamp < currentTimestamp && m.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false), ct);
    }
}