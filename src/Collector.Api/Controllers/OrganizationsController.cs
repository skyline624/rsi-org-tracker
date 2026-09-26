using Collector.Api.Errors;
using Collector.Api.Dtos.Organizations;
using Collector.Api.Dtos.Common;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/organizations")]
[Authorize]
public class OrganizationsController : ControllerBase
{
    private readonly TrackerDbContext _db;
    private readonly IOrganizationMemberRepository _memberRepo;
    private readonly IChangeEventRepository _changeRepo;

    public OrganizationsController(
        TrackerDbContext db,
        IOrganizationMemberRepository memberRepo,
        IChangeEventRepository changeRepo)
    {
        _db = db;
        _memberRepo = memberRepo;
        _changeRepo = changeRepo;
    }

    // Efficient "latest snapshot per org" using INNER JOIN with MAX(Timestamp). The
    // list needs no long text: they are not read (the detail page fetches them).
    private IQueryable<Organization> LatestOrgs() =>
        _db.Organizations.FromSqlRaw("""
            SELECT o.Id, o.Sid, o.Timestamp, o.Name, o.UrlImage, o.UrlCorpo,
                   o.Archetype, o.Lang, o.Commitment, o.Recruiting, o.Roleplay,
                   o.MembersCount, NULL AS Description, NULL AS History,
                   NULL AS Manifesto, NULL AS Charter,
                   o.FocusPrimaryName, o.FocusPrimaryImage, o.FocusSecondaryName,
                   o.FocusSecondaryImage, o.ContentCollected, o.Source
            FROM organizations AS o
            INNER JOIN (
                SELECT Sid, MAX(Timestamp) AS MaxTs
                FROM organizations GROUP BY Sid
            ) AS g ON o.Sid = g.Sid AND o.Timestamp = g.MaxTs
            """);

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<OrganizationDto>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] string? archetype,
        [FromQuery] string? commitment,
        [FromQuery] string? lang,
        [FromQuery] bool? recruiting,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Paging.Page(page);
        pageSize = Paging.PageSize(pageSize);
        var query = LatestOrgs();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // `.Contains()` on SQLite translates to `instr()` which is case-sensitive.
            // `EF.Functions.Like` delegates to SQLite `LIKE` which is case-insensitive
            // for ASCII (thanks to PRAGMA case_sensitive_like=OFF, the default).
            // We escape the user's wildcards (%, _) the same way the user search does.
            var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = $"%{escaped}%";
            query = query.Where(o =>
                EF.Functions.Like(o.Name, pattern, "\\") ||
                EF.Functions.Like(o.Sid, pattern, "\\") ||
                _db.OrgNotes.Any(n => n.OrgSid == o.Sid && EF.Functions.Like(n.Body, pattern, "\\")));
        }
        if (!string.IsNullOrWhiteSpace(archetype))
            query = query.Where(o => o.Archetype == archetype);
        if (!string.IsNullOrWhiteSpace(commitment))
            query = query.Where(o => o.Commitment == commitment);
        if (!string.IsNullOrWhiteSpace(lang))
            query = query.Where(o => o.Lang == lang);
        if (recruiting.HasValue)
            query = query.Where(o => o.Recruiting == recruiting.Value);

        // Whitelisted server-side sort. Unknown keys fall back to the default
        // (Sid ascending), which preserves the legacy behaviour for old clients.
        // Sid is appended as a tie-breaker to keep pagination deterministic when
        // many rows share the same sort key (e.g. identical membersCount).
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        var key = sortBy?.Trim().ToLowerInvariant();
        IOrderedQueryable<Organization> ordered = key switch
        {
            "name"       => desc ? query.OrderByDescending(o => o.Name)         : query.OrderBy(o => o.Name),
            "members"    => desc ? query.OrderByDescending(o => o.MembersCount) : query.OrderBy(o => o.MembersCount),
            "archetype"  => desc ? query.OrderByDescending(o => o.Archetype)    : query.OrderBy(o => o.Archetype),
            "lang"       => desc ? query.OrderByDescending(o => o.Lang)         : query.OrderBy(o => o.Lang),
            "recruiting" => desc ? query.OrderByDescending(o => o.Recruiting)   : query.OrderBy(o => o.Recruiting),
            "sid"        => desc ? query.OrderByDescending(o => o.Sid)          : query.OrderBy(o => o.Sid),
            _            => query.OrderBy(o => o.Sid),
        };
        if (!string.IsNullOrEmpty(key) && key != "sid")
            ordered = ordered.ThenBy(o => o.Sid);

        var total = await query.CountAsync(ct);
        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => MapOrgExpr(o))
            .ToListAsync(ct);

        return Ok(PaginatedResponse<OrganizationDto>.Create(items, page, pageSize, total));
    }

    [HttpGet("{sid}")]
    public async Task<ActionResult<OrganizationDto>> GetBySid(string sid, CancellationToken ct)
    {
        sid = sid.ToUpperInvariant();
        var org = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Organization '{sid}' not found");

        // Listing snapshots (Phase 1) carry no page texts: take them from the latest
        // snapshot that has content. MembersCount is RSI's total, masked members
        // included, kept up to date on the latest snapshot by Phase 3.
        if (!org.ContentCollected)
        {
            var content = await _db.Organizations
                .AsNoTracking()
                .Where(o => o.Sid == sid && o.ContentCollected)
                .OrderByDescending(o => o.Timestamp)
                .FirstOrDefaultAsync(ct);
            if (content != null)
            {
                org.Description = content.Description;
                org.History = content.History;
                org.Manifesto = content.Manifesto;
                org.Charter = content.Charter;
                org.FocusPrimaryName = content.FocusPrimaryName;
                org.FocusPrimaryImage = content.FocusPrimaryImage;
                org.FocusSecondaryName = content.FocusSecondaryName;
                org.FocusSecondaryImage = content.FocusSecondaryImage;
            }
        }

        var dto = MapOrg(org);
        var checks = await _db.DiscoveredOrganizations
            .AsNoTracking()
            .Where(d => d.Sid == sid)
            .Select(d => new { d.ContentCheckedAt, d.LastMembersCollectedAt })
            .FirstOrDefaultAsync(ct);
        dto.ContentCheckedAt = checks?.ContentCheckedAt;
        dto.MembersCollectedAt = checks?.LastMembersCollectedAt;
        return Ok(dto);
    }

    /// <summary>
    /// One page of an org's members, in handle order: current ones (status=active, the
    /// default), former ones or all, each by their latest row. With <c>at_time</c>, the
    /// latest row of each member at that time (status does not apply: a row is only
    /// marked inactive once a later collection supersedes it).
    /// </summary>
    [HttpGet("{sid}/members")]
    public async Task<ActionResult<PaginatedResponse<OrganizationMemberDto>>> GetMembers(
        string sid,
        [FromQuery] DateTime? at_time,
        [FromQuery] string status = "active",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        sid = sid.ToUpperInvariant();
        bool? active = status.ToLowerInvariant() switch
        {
            "active" => true,
            "former" => false,
            "all" => null,
            _ => throw new ValidationException("status must be active, former or all"),
        };
        page = Paging.Page(page);
        pageSize = Paging.PageSize(pageSize);

        // All rows share the same org → one name lookup is enough.
        var orgName = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct);

        if (at_time.HasValue)
        {
            var (thenItems, thenTotal) = await _memberRepo.GetPageAtAsync(sid, at_time.Value.ToUniversalTime(), page, pageSize, ct);
            return Ok(PaginatedResponse<OrganizationMemberDto>.Create(
                thenItems.Select(m => m.ToDto(orgName)).ToList(), page, pageSize, thenTotal));
        }

        var (items, total) = await _memberRepo.GetLatestPageAsync(sid, active, page, pageSize, ct);
        return Ok(PaginatedResponse<OrganizationMemberDto>.Create(
            items.Select(m => m.ToDto(orgName)).ToList(), page, pageSize, total));
    }

    [HttpGet("{sid}/members/changes")]
    public async Task<ActionResult<IReadOnlyList<Dtos.Changes.ChangeEventDto>>> GetMemberChanges(
        string sid,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByOrgSidAsync(sid.ToUpperInvariant(), Paging.Limit(limit), ct);
        return Ok(changes.Select(MapChange).ToList());
    }

    [HttpGet("{sid}/history")]
    public async Task<ActionResult<IReadOnlyList<OrganizationDto>>> GetHistory(
        string sid,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var query = _db.Organizations.AsNoTracking().Where(o => o.Sid == sid.ToUpperInvariant());
        if (from.HasValue) query = query.Where(o => o.Timestamp >= from.Value.ToUniversalTime());
        if (to.HasValue) query = query.Where(o => o.Timestamp <= to.Value.ToUniversalTime());

        // Newest first, at most 500, and only the returned columns (not the page texts).
        var history = await query
            .OrderByDescending(o => o.Timestamp)
            .Take(Paging.Limit(limit))
            .Select(o => MapOrgExpr(o))
            .ToListAsync(ct);
        return Ok(history);
    }

    [HttpGet("{sid}/growth")]
    public async Task<ActionResult<IReadOnlyList<GrowthDataPoint>>> GetGrowth(
        string sid,
        CancellationToken ct = default)
    {
        // Source of truth for headcount is Phase 3 (member_collection_log), not
        // Phase 1 discovery (Organization.MembersCount) — the RSI search endpoint
        // sometimes returns 0 for fully-populated orgs (ex: LIBERASTRA). Each row
        // in member_collection_log represents one member at one collection time,
        // so COUNT(DISTINCT UserHandle) per collection gives the real headcount.
        sid = sid.ToUpperInvariant();

        var perCollection = await _db.MemberCollectionLogs
            .Where(l => l.OrgSid == sid)
            .GroupBy(l => l.CollectionTime)
            .Select(g => new
            {
                g.Key,
                Count = g.Select(x => x.UserHandle).Distinct().Count(),
            })
            .ToListAsync(ct);

        if (perCollection.Count == 0)
            return Ok(Array.Empty<GrowthDataPoint>());

        var byDay = perCollection
            .GroupBy(x => x.Key.Date)
            .OrderBy(g => g.Key)
            // Use the MAX of the day so a mid-day smaller collection doesn't
            // artificially deflate the count (e.g. if a collection was partial).
            .Select(g => new { Date = g.Key, Count = g.Max(x => x.Count) })
            .ToList();

        var growth = new List<GrowthDataPoint>();
        for (var i = 0; i < byDay.Count; i++)
        {
            var prev = i > 0 ? byDay[i - 1].Count : byDay[i].Count;
            growth.Add(new GrowthDataPoint
            {
                Date = byDay[i].Date.ToString("yyyy-MM-dd"),
                MembersCount = byDay[i].Count,
                Delta = byDay[i].Count - prev,
            });
        }
        return Ok(growth);
    }

    // EF-translatable projection
    private static OrganizationDto MapOrgExpr(Organization o) => new()
    {
        Sid = o.Sid,
        Name = o.Name,
        UrlImage = o.UrlImage,
        UrlCorpo = o.UrlCorpo,
        Archetype = o.Archetype,
        Lang = o.Lang,
        Commitment = o.Commitment,
        Recruiting = o.Recruiting,
        Roleplay = o.Roleplay,
        MembersCount = o.MembersCount,
        Timestamp = o.Timestamp,
        Description = o.Description,
        FocusPrimaryName = o.FocusPrimaryName,
        FocusSecondaryName = o.FocusSecondaryName,
    };

    private static OrganizationDto MapOrg(Organization o) => MapOrgExpr(o);

    private static Dtos.Changes.ChangeEventDto MapChange(ChangeEvent e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        ChangeType = e.ChangeType,
        OldValue = e.OldValue,
        NewValue = e.NewValue,
        OrgSid = e.OrgSid,
        UserHandle = e.UserHandle,
    };
}
