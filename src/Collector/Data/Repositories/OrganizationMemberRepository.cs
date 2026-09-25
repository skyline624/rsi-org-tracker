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
                           Rank, RolesJson, UrlImage, IsActive, Stars
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
                       Rank, RolesJson, UrlImage, IsActive, Stars
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

    public async Task<(IReadOnlyList<OrganizationMember> Items, int Total)> GetLatestPageAsync(
        string orgSid, bool? active, int page, int pageSize, CancellationToken ct = default)
    {
        var offset = (page - 1) * pageSize;

        // Current members: the collector keeps exactly their latest row active, so the
        // (OrgSid, IsActive) index serves the page.
        if (active == true)
        {
            var current = DbSet.AsNoTracking().Where(m => m.OrgSid == orgSid && m.IsActive);
            var items = await current
                .OrderBy(m => EF.Functions.Collate(m.UserHandle, "NOCASE"))
                .Skip(offset)
                .Take(pageSize)
                .ToListAsync(ct);
            return (items, await current.CountAsync(ct));
        }

        // Former members, or everyone: latest row per handle (window function, see GetByOrgSidAsync).
        var status = active == false ? 0 : -1;
        var latest = $@"
            SELECT Id, OrgSid, UserHandle, CitizenId, Timestamp, DisplayName,
                   Rank, RolesJson, UrlImage, IsActive, Stars
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (PARTITION BY UserHandle ORDER BY Timestamp DESC) AS _rn
                FROM organization_members
                WHERE OrgSid = {{0}}
            )
            WHERE _rn = 1 AND ({{1}} = -1 OR IsActive = {{1}})";

        var items_ = await DbSet
            .FromSqlRaw(latest + " ORDER BY UserHandle COLLATE NOCASE LIMIT {2} OFFSET {3}", orgSid, status, pageSize, offset)
            .AsNoTracking()
            .ToListAsync(ct);
        var total = await Context.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM ({latest})", orgSid, status)
            .SingleAsync(ct);
        return (items_, total);
    }

    public async Task<IReadOnlyList<string>> GetOrgSidsForHandleAsync(
        string userHandle, bool activeOnly, CancellationToken ct = default)
    {
        return await DbSet
            .Where(m => m.UserHandle == userHandle && (!activeOnly || m.IsActive))
            .Select(m => m.OrgSid)
            .Distinct()
            .ToListAsync(ct);
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