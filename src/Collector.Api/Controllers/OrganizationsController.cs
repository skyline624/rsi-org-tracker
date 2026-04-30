using Collector.Api.Dtos.Organizations;
using Collector.Api.Dtos.Common;
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

    // Efficient "latest snapshot per org" using INNER JOIN with MAX(Timestamp)
    private IQueryable<Organization> LatestOrgs() =>
        _db.Organizations.FromSqlRaw("""
            SELECT o.Id, o.Sid, o.Timestamp, o.Name, o.UrlImage, o.UrlCorpo,
                   o.Archetype, o.Lang, o.Commitment, o.Recruiting, o.Roleplay,
                   o.MembersCount, o.Description, o.History, o.Manifesto, o.Charter,
                   o.FocusPrimaryName, o.FocusPrimaryImage, o.FocusSecondaryName,
                   o.FocusSecondaryImage, o.ContentCollected
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
                EF.Functions.Like(o.Sid, pattern, "\\"));
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
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Organization '{sid}' not found");

        // Override the stale Phase 1 MembersCount with the real Phase 3 headcount.
        // Some orgs have MembersCount=0 from the RSI search API even though they
        // have an active roster — the truth is in member_collection_log.
        var latestCollection = await _db.MemberCollectionLogs
            .Where(l => l.OrgSid == sid)
            .OrderByDescending(l => l.CollectionTime)
            .Select(l => l.CollectionTime)
            .FirstOrDefaultAsync(ct);

        var dto = MapOrg(org);
        if (latestCollection != default)
        {
            var realCount = await _db.MemberCollectionLogs
                .Where(l => l.OrgSid == sid && l.CollectionTime == latestCollection)
                .Select(l => l.UserHandle)
                .Distinct()
                .CountAsync(ct);
            if (realCount > 0)
            {
                dto.MembersCount = realCount;
            }
        }

        return Ok(dto);
    }

    [HttpGet("{sid}/members")]
    public async Task<ActionResult<IReadOnlyList<OrganizationMemberDto>>> GetMembers(
        string sid,
        [FromQuery] DateTime? at_time,
        [FromQuery] bool include_inactive = false,
        CancellationToken ct = default)
    {
        sid = sid.ToUpperInvariant();

        if (at_time.HasValue)
        {
            var members = await _memberRepo.GetByOrgSidAsync(sid, at_time.Value.ToUniversalTime(), ct);
            return Ok(members.Select(MapMember).ToList());
        }

        if (!include_inactive)
        {
            var active = await _db.OrganizationMembers
                .Where(m => m.OrgSid == sid && m.IsActive)
                .ToListAsync(ct);
            return Ok(active.Select(MapMember).ToList());
        }

        // include_inactive=true: latest snapshot per member (active or former)
        var all = await _memberRepo.GetByOrgSidAsync(sid, null, ct);
        return Ok(all.Select(MapMember).ToList());
    }

    [HttpGet("{sid}/members/changes")]
    public async Task<ActionResult<IReadOnlyList<Dtos.Changes.ChangeEventDto>>> GetMemberChanges(
        string sid,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByOrgSidAsync(sid.ToUpperInvariant(), limit, ct);
        return Ok(changes.Select(MapChange).ToList());
    }

    [HttpGet("{sid}/history")]
    public async Task<ActionResult<IReadOnlyList<OrganizationDto>>> GetHistory(
        string sid,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var query = _db.Organizations.Where(o => o.Sid == sid.ToUpperInvariant());
        if (from.HasValue) query = query.Where(o => o.Timestamp >= from.Value.ToUniversalTime());
        if (to.HasValue) query = query.Where(o => o.Timestamp <= to.Value.ToUniversalTime());

        var history = await query.OrderByDescending(o => o.Timestamp).ToListAsync(ct);
        return Ok(history.Select(MapOrg).ToList());
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

    private static OrganizationMemberDto MapMember(OrganizationMember m) => new()
    {
        UserHandle = m.UserHandle,
        CitizenId = m.CitizenId,
        DisplayName = m.DisplayName,
        Rank = m.Rank,
        UrlImage = m.UrlImage,
        Timestamp = m.Timestamp,
        IsActive = m.IsActive,
    };

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
