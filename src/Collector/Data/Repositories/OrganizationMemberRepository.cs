using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrganizationMemberRepository : Repository<OrganizationMember>, IOrganizationMemberRepository
{
    public OrganizationMemberRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<OrganizationMember>> GetActiveByOrgSidAsync(string orgSid, CancellationToken ct = default)
        => await DbSet.AsNoTracking().Where(m => m.OrgSid == orgSid && m.IsActive).ToListAsync(ct);

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

        // Former members, or everyone. Handles come from the (OrgSid, UserHandle) index
        // alone, former ones being those without an active row; only the page's handles
        // then have their rows read to find the latest. A window over every row of the
        // org took 6.7 s for TEST (442 k rows) on the production copy, this ~0.2 s.
        // NOT IN, not EXCEPT: SQLite 3.53 merges an EXCEPT through the (OrgSid, UserHandle)
        // index and reads every row to test IsActive (5 s for TEST), while the NOT IN
        // list is built once from the (OrgSid, IsActive) index (0.1 s).
        var handles = active == false
            ? @"SELECT UserHandle FROM organization_members WHERE OrgSid = {0} GROUP BY UserHandle
                HAVING UserHandle NOT IN (
                    SELECT UserHandle FROM organization_members WHERE OrgSid = {0} AND IsActive = 1)"
            : "SELECT UserHandle FROM organization_members WHERE OrgSid = {0} GROUP BY UserHandle";

        var pageRows = await DbSet
            .FromSqlRaw($@"
                SELECT Id, OrgSid, UserHandle, CitizenId, Timestamp, DisplayName,
                       Rank, RolesJson, UrlImage, IsActive, Stars
                FROM (
                    SELECT m.*,
                           ROW_NUMBER() OVER (PARTITION BY m.UserHandle ORDER BY m.Timestamp DESC) AS _rn
                    FROM organization_members m
                    WHERE m.OrgSid = {{0}} AND m.UserHandle IN (
                        SELECT UserHandle FROM ({handles})
                        ORDER BY UserHandle COLLATE NOCASE LIMIT {{1}} OFFSET {{2}})
                )
                WHERE _rn = 1
                ORDER BY UserHandle COLLATE NOCASE", orgSid, pageSize, offset)
            .AsNoTracking()
            .ToListAsync(ct);
        var total = await Context.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM ({handles})", orgSid)
            .SingleAsync(ct);
        return (pageRows, total);
    }

    public async Task<(IReadOnlyList<OrganizationMember> Items, int Total)> GetPageAtAsync(
        string orgSid, DateTime asOf, int page, int pageSize, CancellationToken ct = default)
    {
        // Same shape as GetLatestPageAsync, up to asOf: the page's handles first, then
        // only their rows are read (at_time used to load the org's whole history).
        const string handles =
            "SELECT UserHandle FROM organization_members WHERE OrgSid = {0} AND Timestamp <= {3} GROUP BY UserHandle";
        var offset = (page - 1) * pageSize;
        var pageRows = await DbSet
            .FromSqlRaw($@"
                SELECT Id, OrgSid, UserHandle, CitizenId, Timestamp, DisplayName,
                       Rank, RolesJson, UrlImage, IsActive, Stars
                FROM (
                    SELECT m.*,
                           ROW_NUMBER() OVER (PARTITION BY m.UserHandle ORDER BY m.Timestamp DESC) AS _rn
                    FROM organization_members m
                    WHERE m.OrgSid = {{0}} AND m.Timestamp <= {{3}} AND m.UserHandle IN (
                        SELECT UserHandle FROM ({handles})
                        ORDER BY UserHandle COLLATE NOCASE LIMIT {{1}} OFFSET {{2}})
                )
                WHERE _rn = 1
                ORDER BY UserHandle COLLATE NOCASE", orgSid, pageSize, offset, asOf)
            .AsNoTracking()
            .ToListAsync(ct);
        var total = await Context.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM ({handles})", orgSid, pageSize, offset, asOf)
            .SingleAsync(ct);
        return (pageRows, total);
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> GetFirstSeenByOrgAsync(
        string userHandle, CancellationToken ct = default)
    {
        return await DbSet
            .Where(m => m.UserHandle == userHandle)
            .GroupBy(m => m.OrgSid)
            .Select(g => new { OrgSid = g.Key, FirstSeen = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.OrgSid, x => x.FirstSeen, ct);
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