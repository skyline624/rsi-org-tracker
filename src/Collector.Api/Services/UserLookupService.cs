using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Users;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

/// <summary>The latest roster row of a handle in one org, the org's latest name, and the handle's first appearance there.</summary>
public sealed record MembershipRow(OrganizationMember Latest, string? OrgName, DateTime? FirstSeen);

/// <summary>Player queries shared by the site (UsersController) and the Discord bot (BotController).</summary>
public sealed class UserLookupService(TrackerDbContext db, IOrganizationRepository orgRepo)
{
    public const int MinSearchLength = 2;
    public const int MaxCountedMatches = 1001;

    /// <summary>
    /// Searches enriched citizens AND roster-only members (handles tracked in
    /// organization_members that never got a CitizenId, so they can't exist in
    /// `users` — e.g. RSI accounts that hide their citizen number). EF Core cannot
    /// translate a UNION of these two differently-shaped projections under SQLite, so
    /// the combined set is expressed as raw SQL, which also lets the DB do the paging.
    /// </summary>
    public async Task<PaginatedResponse<UserProfileDto>> SearchAsync(
        string search, int page, int pageSize, CancellationToken ct)
    {
        // A single character would match a large share of 32 M roster rows.
        if (search.Trim().Length < MinSearchLength)
        {
            return PaginatedResponse<UserProfileDto>.Create(Array.Empty<UserProfileDto>(), page, pageSize, 0);
        }

        // Enriched side: substring match (full scan of the smaller `users` table is cheap).
        // Escape %/_ so user input can't pivot into wildcards (requires the ESCAPE clause).
        var escaped = search.Replace("%", "\\%").Replace("_", "\\_");
        var substring = $"%{escaped}%";

        // Non-enriched side: PREFIX match only. SQLite can use an index for a LIKE prefix
        // solely when the column collates NOCASE *and* there's no ESCAPE clause — that's
        // what IX_organization_members_UserHandle_NoCase exists for. A substring there
        // would force a full scan of the ~12M-row snapshot table (tens of seconds). '%'
        // can't appear in a handle, so stripping it (not escaping) keeps the prefix clean;
        // an empty prefix becomes a guaranteed no-match rather than a match-all scan.
        var prefixTerm = search.Trim().Replace("%", "");
        var prefix = prefixTerm.Length == 0 ? "" : prefixTerm + "%";

        // Numeric term → also match by citizen id (enriched users + tracked entities).
        // -1 can never match a real citizen id (all are > 0), so a non-numeric term is a no-op here.
        var citizenId = int.TryParse(search.Trim(), out var cid) && cid > 0 ? cid : -1;

        // Shared UNION body. {0} = substring pattern (enriched), {1} = prefix pattern (members).
        const string union = @"
            SELECT CitizenId, UserHandle, DisplayName, UrlImage, Bio, Location, Enlisted, UpdatedAt, 1 AS IsEnriched
            FROM users
            WHERE UserHandle LIKE {0} ESCAPE '\' OR (DisplayName IS NOT NULL AND DisplayName LIKE {0} ESCAPE '\')
               OR CitizenId = {2}
            UNION ALL
            SELECT 0 AS CitizenId, m.UserHandle, m.DisplayName, m.UrlImage,
                   NULL AS Bio, NULL AS Location, NULL AS Enlisted, m.Timestamp AS UpdatedAt, 0 AS IsEnriched
            FROM organization_members m
            WHERE m.UserHandle LIKE {1}
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = m.UserHandle)
              AND NOT EXISTS (SELECT 1 FROM organization_members o
                              WHERE o.UserHandle = m.UserHandle AND o.Timestamp > m.Timestamp)
            UNION ALL
            SELECT COALESCE(e.CitizenId, 0) AS CitizenId, e.CurrentHandle AS UserHandle, e.DisplayName,
                   NULL AS UrlImage, NULL AS Bio, NULL AS Location, NULL AS Enlisted, e.UpdatedAt, 0 AS IsEnriched
            FROM tracked_entities e
            WHERE e.CurrentHandle IS NOT NULL
              AND (e.CurrentHandle LIKE {0} ESCAPE '\' OR (e.DisplayName IS NOT NULL AND e.DisplayName LIKE {0} ESCAPE '\'))
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = e.CurrentHandle)
            UNION ALL
            SELECT COALESCE(en.CitizenId, 0) AS CitizenId, en.CurrentHandle AS UserHandle, en.DisplayName,
                   NULL AS UrlImage, NULL AS Bio, NULL AS Location, NULL AS Enlisted, en.UpdatedAt, 0 AS IsEnriched
            FROM tracked_entities en
            WHERE en.CurrentHandle IS NOT NULL
              AND NOT (en.CurrentHandle LIKE {0} ESCAPE '\' OR (en.DisplayName IS NOT NULL AND en.DisplayName LIKE {0} ESCAPE '\'))
              AND EXISTS (SELECT 1 FROM entity_notes nt WHERE nt.TrackedEntityId = en.Id AND nt.Body LIKE {0} ESCAPE '\')
            UNION ALL
            SELECT ec.CitizenId, ec.CurrentHandle AS UserHandle, ec.DisplayName,
                   NULL AS UrlImage, NULL AS Bio, NULL AS Location, NULL AS Enlisted, ec.UpdatedAt, 0 AS IsEnriched
            FROM tracked_entities ec
            WHERE ec.CitizenId = {2} AND ec.CurrentHandle IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM users u WHERE u.UserHandle = ec.CurrentHandle)";

        // Counting stops at MaxCountedMatches: the front shows "more than 1000", and
        // SQLite stops producing the union there instead of counting every match.
        var total = await db.Database
            .SqlQueryRaw<int>(
                $"SELECT COUNT(*) AS Value FROM (SELECT 1 FROM ({union}) LIMIT {MaxCountedMatches})",
                substring, prefix, citizenId)
            .SingleAsync(ct);

        var pageSql = $"SELECT * FROM ({union}) ORDER BY UserHandle COLLATE NOCASE LIMIT {{3}} OFFSET {{4}}";
        var items = await db.Database
            .SqlQueryRaw<UserProfileDto>(pageSql, substring, prefix, citizenId, pageSize, (page - 1) * pageSize)
            .ToListAsync(ct);

        // Raw SQL mapped onto a DTO skips the model's UTC conversion: mark the dates here.
        foreach (var item in items)
        {
            item.UpdatedAt = DateTime.SpecifyKind(item.UpdatedAt, DateTimeKind.Utc);
            if (item.Enlisted is { } enlisted) item.Enlisted = DateTime.SpecifyKind(enlisted, DateTimeKind.Utc);
        }

        return PaginatedResponse<UserProfileDto>.Create(items, page, pageSize, total);
    }

    /// <summary>Each org of the handle with its latest row; former ones only when asked.</summary>
    public async Task<IReadOnlyList<MembershipRow>> GetMembershipsAsync(
        string handle, bool includeInactive, CancellationToken ct)
    {
        var memberships = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToListAsync(ct);

        if (!includeInactive)
            memberships = memberships.Where(m => m.IsActive).ToList();

        // "Member since" = first snapshot in which this handle appeared in each org.
        // A plain GroupBy + Min aggregate (unlike the First() projection above) so it
        // stays a single translatable query.
        var firstSeen = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => new { OrgSid = g.Key, First = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.OrgSid, x => x.First, ct);

        // Resolve the latest known name for each org so the frontend can show
        // "SID — Name" instead of just the SID.
        var orgSids = memberships.Select(m => m.OrgSid).Distinct().ToList();
        var orgNames = await orgRepo.GetLatestNamesBySidsAsync(orgSids, ct);

        return memberships
            .Select(m => new MembershipRow(
                m,
                orgNames.GetValueOrDefault(m.OrgSid),
                firstSeen.TryGetValue(m.OrgSid, out var since) ? since : null))
            .ToList();
    }

    /// <summary>
    /// The handle as the tracker stores it, for a handle typed by someone: as typed in
    /// users; else ignoring case in rosters (IX_organization_members_UserHandle_NoCase);
    /// else a former handle of a citizen, which leads to the current one. Null if unknown.
    /// </summary>
    public async Task<string?> ResolveHandleAsync(string input, CancellationToken ct)
    {
        var handle = input.Trim();
        if (handle.Length == 0) return null;

        var exact = await db.Users.AsNoTracking()
            .Where(u => u.UserHandle == handle)
            .Select(u => u.UserHandle)
            .FirstOrDefaultAsync(ct);
        if (exact != null) return exact;

        var roster = await db.OrganizationMembers.AsNoTracking()
            .Where(m => EF.Functions.Collate(m.UserHandle, "NOCASE") == handle)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.UserHandle)
            .FirstOrDefaultAsync(ct);
        if (roster != null) return roster;

        var citizenId = await db.UserHandleHistories.AsNoTracking()
            .Where(h => EF.Functions.Collate(h.UserHandle, "NOCASE") == handle)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => (int?)h.CitizenId)
            .FirstOrDefaultAsync(ct);
        if (citizenId is null) return null;

        return await db.Users.AsNoTracking()
            .Where(u => u.CitizenId == citizenId)
            .Select(u => u.UserHandle)
            .FirstOrDefaultAsync(ct);
    }
}
