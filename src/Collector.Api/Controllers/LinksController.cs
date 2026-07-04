using Collector.Api.Auth;
using Collector.Api.Dtos.Links;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>Links from a person to their profile on other tools (UEX, Discord, …).</summary>
[ApiController]
[Route("api")]
[Authorize]
public class LinksController : ControllerBase
{
    private readonly IEntityLinkRepository _links;
    private readonly ITrackedEntityRepository _entities;
    private readonly IUserRepository _users;
    private readonly IEntityResolver _resolver;
    private readonly CurrentUserAccessor _currentUser;

    public LinksController(
        IEntityLinkRepository links,
        ITrackedEntityRepository entities,
        IUserRepository users,
        IEntityResolver resolver,
        CurrentUserAccessor currentUser)
    {
        _links = links;
        _entities = entities;
        _users = users;
        _resolver = resolver;
        _currentUser = currentUser;
    }

    [HttpGet("users/{handle}/links")]
    public async Task<ActionResult<IReadOnlyList<LinkDto>>> GetLinks(string handle, CancellationToken ct)
    {
        var entity = await ResolveExistingAsync(handle, ct);
        if (entity is null) return Ok(new List<LinkDto>());
        var links = await _links.GetByEntityAsync(entity.Id, ct);
        return Ok(links.Select(ToDto).ToList());
    }

    /// <summary>Adds or updates the link for a provider (one per provider), creating the entity if needed.</summary>
    [HttpPost("users/{handle}/links")]
    public async Task<ActionResult<LinkDto>> CreateLink(string handle, [FromBody] CreateLinkRequest req, CancellationToken ct)
    {
        var provider = req.Provider.Trim().ToLowerInvariant();
        if (!LinkProviders.IsValid(provider)) return BadRequest(new { message = "Fournisseur inconnu." });
        var value = req.Value.Trim();
        if (value.Length == 0) return BadRequest(new { message = "Valeur vide." });

        var user = await _users.GetByHandleAsync(handle, ct);
        var entityId = await _resolver.ResolveOrCreateAsync(user?.CitizenId, handle, user?.DisplayName, ct);

        // Idempotent: an identical (provider, value) is returned as-is; a different
        // value is added alongside (multiple accounts per provider are allowed).
        var existing = await _links.GetByEntityProviderValueAsync(entityId, provider, value, ct);
        if (existing is not null) return Ok(ToDto(existing));

        var now = DateTime.UtcNow;
        var link = new EntityLink
        {
            TrackedEntityId = entityId,
            Provider = provider,
            Value = value,
            AuthorApiUserId = _currentUser.UserId ?? 0,
            AuthorUsername = _currentUser.Username ?? "unknown",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _links.AddAsync(link, ct);
        await _links.SaveChangesAsync(ct);
        return Ok(ToDto(link));
    }

    [HttpDelete("links/{id:long}")]
    public async Task<IActionResult> DeleteLink(long id, CancellationToken ct)
    {
        var link = await _links.GetByIdAsync(id, ct);
        if (link is null) return NotFound();
        if (!_currentUser.IsAdmin && link.AuthorApiUserId != (_currentUser.UserId ?? -1)) return Forbid();

        _links.Remove(link);
        await _links.SaveChangesAsync(ct);
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

    private static LinkDto ToDto(EntityLink l) => new()
    {
        Id = l.Id,
        Provider = l.Provider,
        Value = l.Value,
        AuthorUsername = l.AuthorUsername,
        CreatedAt = l.CreatedAt,
    };
}
