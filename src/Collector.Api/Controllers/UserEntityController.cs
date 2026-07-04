using Collector.Api.Dtos.Admin;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Reads / edits the tool's internal identity for a person: notably assigning a
/// citizen id to someone who has none (redacted / roster-only accounts).
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class UserEntityController : ControllerBase
{
    private readonly IEntityResolver _resolver;
    private readonly ITrackedEntityRepository _entities;
    private readonly IUserRepository _users;

    public UserEntityController(IEntityResolver resolver, ITrackedEntityRepository entities, IUserRepository users)
    {
        _resolver = resolver;
        _entities = entities;
        _users = users;
    }

    /// <summary>Current internal entity for a handle (null if none exists yet).</summary>
    [HttpGet("users/{handle}/entity")]
    public async Task<ActionResult<TrackedEntityDto?>> GetEntity(string handle, CancellationToken ct)
    {
        var entity = await ResolveExistingAsync(handle, ct);
        return Ok(entity is null ? null : ToDto(entity));
    }

    /// <summary>Assigns (or corrects) the citizen id for a person, creating the entity if needed.</summary>
    [HttpPut("users/{handle}/citizen-id")]
    public async Task<ActionResult<TrackedEntityDto>> SetCitizenId(
        string handle, [FromBody] SetCitizenIdRequest req, CancellationToken ct)
    {
        // Refuse if another person already holds this citizen id (the unique index would reject it anyway).
        var holder = await _entities.GetByCitizenIdAsync(req.CitizenId, ct);

        var user = await _users.GetByHandleAsync(handle, ct);
        var entityId = await _resolver.ResolveOrCreateAsync(user?.CitizenId, handle, user?.DisplayName, ct);
        var entity = await _entities.GetByIdAsync(entityId, ct);
        if (entity is null) return NotFound();

        if (holder is not null && holder.Id != entity.Id)
            return Conflict(new { message = $"Le citizen id {req.CitizenId} est déjà attribué à une autre personne." });

        entity.CitizenId = req.CitizenId;
        entity.UpdatedAt = DateTime.UtcNow;
        await _entities.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
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

    private static TrackedEntityDto ToDto(TrackedEntity e) => new()
    {
        Id = e.Id,
        CitizenId = e.CitizenId,
        CurrentHandle = e.CurrentHandle,
        DisplayName = e.DisplayName,
        Source = e.Source,
        Status = e.Status,
        CreatedAt = e.CreatedAt,
    };
}
