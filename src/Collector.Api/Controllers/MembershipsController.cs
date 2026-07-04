using Collector.Api.Auth;
using Collector.Api.Dtos.Memberships;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Manual "person belongs to org" links, kept separate from the collected roster so
/// the collector never overwrites them. Notably records people known through an org's
/// Discord who don't appear on its RSI page.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class MembershipsController : ControllerBase
{
    private readonly IEntityResolver _resolver;
    private readonly ITrackedEntityRepository _entities;
    private readonly IEntityMembershipRepository _memberships;
    private readonly IUserRepository _users;
    private readonly IOrganizationRepository _orgs;
    private readonly CurrentUserAccessor _currentUser;

    public MembershipsController(
        IEntityResolver resolver,
        ITrackedEntityRepository entities,
        IEntityMembershipRepository memberships,
        IUserRepository users,
        IOrganizationRepository orgs,
        CurrentUserAccessor currentUser)
    {
        _resolver = resolver;
        _entities = entities;
        _memberships = memberships;
        _users = users;
        _orgs = orgs;
        _currentUser = currentUser;
    }

    [HttpGet("users/{handle}/memberships")]
    public async Task<ActionResult<IReadOnlyList<MembershipDto>>> GetMemberships(string handle, CancellationToken ct)
    {
        var entity = await ResolveExistingAsync(handle, ct);
        if (entity is null) return Ok(Array.Empty<MembershipDto>());

        var rows = await _memberships.GetByEntityIdAsync(entity.Id, ct);
        if (rows.Count == 0) return Ok(Array.Empty<MembershipDto>());

        var names = await _orgs.GetLatestBySidsAsync(rows.Select(r => r.OrgSid).Distinct().ToList(), ct);
        return Ok(rows.Select(r => ToDto(r, names.TryGetValue(r.OrgSid, out var o) ? o.Name : null)).ToList());
    }

    [HttpPost("users/{handle}/memberships")]
    public async Task<ActionResult<MembershipDto>> CreateMembership(
        string handle, [FromBody] CreateMembershipRequest req, CancellationToken ct)
    {
        var sid = req.OrgSid.Trim();
        var via = string.IsNullOrWhiteSpace(req.Via) ? MembershipVia.Discord : req.Via.Trim().ToLowerInvariant();
        if (!MembershipVia.IsValid(via))
            return BadRequest(new { message = "Via must be one of: rsi, discord, both." });

        // The org must be known (collected or manually added) so links point to real orgs.
        var org = await _orgs.GetLatestBySidAsync(sid, ct);
        if (org is null)
            return NotFound(new { message = $"Unknown organization '{sid}'. Add it first (manual entry)." });

        var user = await _users.GetByHandleAsync(handle, ct);
        var entityId = await _resolver.ResolveOrCreateAsync(user?.CitizenId, handle, user?.DisplayName, ct);

        var rank = string.IsNullOrWhiteSpace(req.Rank) ? null : req.Rank.Trim();
        var since = req.SinceDate ?? DateTime.UtcNow;

        // Upsert: one manual link per (person, org).
        var existing = await _memberships.GetByEntityAndOrgAsync(entityId, sid, ct);
        if (existing is not null)
        {
            existing.Rank = rank;
            existing.Via = via;
            existing.SinceDate = since;
            await _memberships.SaveChangesAsync(ct);
            return Ok(ToDto(existing, org.Name));
        }

        var row = new EntityMembership
        {
            TrackedEntityId = entityId,
            OrgSid = sid,
            Rank = rank,
            Via = via,
            SinceDate = since,
            AuthorApiUserId = _currentUser.UserId ?? 0,
            AuthorUsername = _currentUser.Username ?? "unknown",
            CreatedAt = DateTime.UtcNow,
        };
        await _memberships.AddAsync(row, ct);
        await _memberships.SaveChangesAsync(ct);
        return Ok(ToDto(row, org.Name));
    }

    [HttpDelete("memberships/{id:long}")]
    public async Task<IActionResult> DeleteMembership(long id, CancellationToken ct)
    {
        var row = await _memberships.GetByIdAsync(id, ct);
        if (row is null) return NotFound();
        if (!(_currentUser.IsAdmin || row.AuthorApiUserId == (_currentUser.UserId ?? -1)))
            return Forbid();

        _memberships.Remove(row);
        await _memberships.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<TrackedEntity?> ResolveExistingAsync(string handle, CancellationToken ct)
    {
        var user = await _users.GetByHandleAsync(handle, ct);
        if (user is not null)
        {
            var byCid = await _entities.GetByCitizenIdAsync(user.CitizenId, ct);
            if (byCid is not null) return byCid;
        }
        return await _entities.GetByHandleAsync(handle, ct);
    }

    private static MembershipDto ToDto(EntityMembership m, string? orgName) => new()
    {
        Id = m.Id,
        OrgSid = m.OrgSid,
        OrgName = orgName,
        Rank = m.Rank,
        Via = m.Via,
        SinceDate = m.SinceDate,
        AuthorUsername = m.AuthorUsername,
        CreatedAt = m.CreatedAt,
    };
}
