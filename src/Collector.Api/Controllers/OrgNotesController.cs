using Collector.Api.Auth;
using Collector.Api.Dtos.Notes;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>Free-text notes attached to an organization (by SID).</summary>
[ApiController]
[Route("api")]
[Authorize]
public class OrgNotesController : ControllerBase
{
    private readonly IOrgNoteRepository _notes;
    private readonly IOrganizationRepository _orgs;
    private readonly CurrentUserAccessor _currentUser;

    public OrgNotesController(IOrgNoteRepository notes, IOrganizationRepository orgs, CurrentUserAccessor currentUser)
    {
        _notes = notes;
        _orgs = orgs;
        _currentUser = currentUser;
    }

    [HttpGet("organizations/{sid}/notes")]
    public async Task<ActionResult<IReadOnlyList<OrgNoteDto>>> GetNotes(string sid, CancellationToken ct)
    {
        var notes = await _notes.GetByOrgSidAsync(sid, ct);
        return Ok(notes.Select(ToDto).ToList());
    }

    [HttpPost("organizations/{sid}/notes")]
    public async Task<ActionResult<OrgNoteDto>> CreateNote(string sid, [FromBody] CreateOrgNoteRequest req, CancellationToken ct)
    {
        // Canonicalise the SID to upper-case (project convention) so validation and
        // storage match regardless of the capitalisation in the URL.
        sid = sid.Trim().ToUpperInvariant();
        var org = await _orgs.GetLatestBySidAsync(sid, ct);
        if (org is null) return NotFound(new { message = "Unknown organization." });

        var now = DateTime.UtcNow;
        var note = new OrgNote
        {
            OrgSid = sid,
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

    [HttpPut("org-notes/{id:long}")]
    public async Task<ActionResult<OrgNoteDto>> UpdateNote(long id, [FromBody] UpdateOrgNoteRequest req, CancellationToken ct)
    {
        var note = await _notes.GetByIdAsync(id, ct);
        if (note is null) return NotFound();
        if (!CanModify(note)) return Forbid();

        note.Body = req.Body.Trim();
        note.UpdatedAt = DateTime.UtcNow;
        await _notes.SaveChangesAsync(ct);
        return Ok(ToDto(note));
    }

    [HttpDelete("org-notes/{id:long}")]
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
    private bool CanModify(OrgNote note)
        => _currentUser.IsAdmin || note.AuthorApiUserId == (_currentUser.UserId ?? -1);

    private static OrgNoteDto ToDto(OrgNote n) => new()
    {
        Id = n.Id,
        OrgSid = n.OrgSid,
        AuthorApiUserId = n.AuthorApiUserId,
        AuthorUsername = n.AuthorUsername,
        Body = n.Body,
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt,
    };
}
