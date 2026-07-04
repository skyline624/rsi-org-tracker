using Collector.Api.Auth;
using Collector.Api.Dtos.Notes;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Free-text notes attached to a person (via the stable <see cref="TrackedEntity"/>).
/// Notes are keyed by handle for the UI's convenience; the entity is resolved (and
/// lazily created on first note) so notes survive handle changes and work for
/// redacted / roster-only people too.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class NotesController : ControllerBase
{
    private readonly IEntityResolver _resolver;
    private readonly ITrackedEntityRepository _entities;
    private readonly IEntityNoteRepository _notes;
    private readonly IUserRepository _users;
    private readonly CurrentUserAccessor _currentUser;

    public NotesController(
        IEntityResolver resolver,
        ITrackedEntityRepository entities,
        IEntityNoteRepository notes,
        IUserRepository users,
        CurrentUserAccessor currentUser)
    {
        _resolver = resolver;
        _entities = entities;
        _notes = notes;
        _users = users;
        _currentUser = currentUser;
    }

    /// <summary>Notes for a person (empty if the entity doesn't exist yet).</summary>
    [HttpGet("users/{handle}/notes")]
    public async Task<ActionResult<IReadOnlyList<NoteDto>>> GetNotes(string handle, CancellationToken ct)
    {
        var entity = await ResolveExistingAsync(handle, ct);
        if (entity is null) return Ok(Array.Empty<NoteDto>());
        var notes = await _notes.GetByEntityIdAsync(entity.Id, ct);
        return Ok(notes.Select(ToDto).ToList());
    }

    /// <summary>Adds a note, creating the tracked entity on first use.</summary>
    [HttpPost("users/{handle}/notes")]
    public async Task<ActionResult<NoteDto>> CreateNote(string handle, [FromBody] CreateNoteRequest req, CancellationToken ct)
    {
        var user = await _users.GetByHandleAsync(handle, ct);
        var entityId = await _resolver.ResolveOrCreateAsync(user?.CitizenId, handle, user?.DisplayName, ct);

        var now = DateTime.UtcNow;
        var note = new EntityNote
        {
            TrackedEntityId = entityId,
            AuthorApiUserId = _currentUser.UserId ?? 0,
            AuthorUsername = _currentUser.Username ?? "unknown",
            Body = req.Body.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _notes.AddAsync(note, ct);
        await _notes.SaveChangesAsync(ct);
        return Ok(ToDto(note));
    }

    [HttpPut("notes/{id:long}")]
    public async Task<ActionResult<NoteDto>> UpdateNote(long id, [FromBody] UpdateNoteRequest req, CancellationToken ct)
    {
        var note = await _notes.GetByIdAsync(id, ct);
        if (note is null) return NotFound();
        if (!CanModify(note)) return Forbid();

        note.Body = req.Body.Trim();
        note.UpdatedAt = DateTime.UtcNow;
        await _notes.SaveChangesAsync(ct);
        return Ok(ToDto(note));
    }

    [HttpDelete("notes/{id:long}")]
    public async Task<IActionResult> DeleteNote(long id, CancellationToken ct)
    {
        var note = await _notes.GetByIdAsync(id, ct);
        if (note is null) return NotFound();
        if (!CanModify(note)) return Forbid();

        _notes.Remove(note);
        await _notes.SaveChangesAsync(ct);
        return NoContent();
    }

    // Only the author or an admin may edit/delete a note.
    private bool CanModify(EntityNote note)
        => _currentUser.IsAdmin || note.AuthorApiUserId == (_currentUser.UserId ?? -1);

    // Resolve an EXISTING entity for a handle without creating one (for reads).
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

    private static NoteDto ToDto(EntityNote n) => new()
    {
        Id = n.Id,
        TrackedEntityId = n.TrackedEntityId,
        AuthorApiUserId = n.AuthorApiUserId,
        AuthorUsername = n.AuthorUsername,
        Body = n.Body,
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt,
    };
}
