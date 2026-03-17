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
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = LatestOrgs();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(o => o.Name.Contains(search) || o.Sid.Contains(search));
        if (!string.IsNullOrWhiteSpace(archetype))
            query = query.Where(o => o.Archetype == archetype);
        if (!string.IsNullOrWhiteSpace(commitment))
            query = query.Where(o => o.Commitment == commitment);
        if (!string.IsNullOrWhiteSpace(lang))
            query = query.Where(o => o.Lang == lang);
        if (recruiting.HasValue)
            query = query.Where(o => o.Recruiting == recruiting.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(o => o.Sid)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => MapOrgExpr(o))
            .ToListAsync(ct);

        return Ok(PaginatedResponse<OrganizationDto>.Create(items, page, pageSize, total));
    }

    [HttpGet("{sid}")]
    public async Task<ActionResult<OrganizationDto>> GetBySid(string sid, CancellationToken ct)
    {
        var org = await _db.Organizations
            .Where(o => o.Sid == sid.ToUpperInvariant())
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Organization '{sid}' not found");
        return Ok(MapOrg(org));
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
        var history = await _db.Organizations
            .Where(o => o.Sid == sid.ToUpperInvariant())
            .OrderBy(o => o.Timestamp)
            .Select(o => new { o.Timestamp, o.MembersCount })
            .ToListAsync(ct);

        if (!history.Any()) return Ok(Array.Empty<GrowthDataPoint>());

        var byDay = history
            .GroupBy(o => o.Timestamp.Date)
            .OrderBy(g => g.Key)
            .Select(g => new { Date = g.Key, Count = (int)g.Average(o => o.MembersCount) })
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
