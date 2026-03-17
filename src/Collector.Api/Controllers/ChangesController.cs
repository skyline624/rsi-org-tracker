using Collector.Api.Dtos.Changes;
using Collector.Data;
using Collector.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/changes")]
[Authorize]
public class ChangesController : ControllerBase
{
    private readonly TrackerDbContext _db;
    private readonly IChangeEventRepository _changeRepo;

    public ChangesController(TrackerDbContext db, IChangeEventRepository changeRepo)
    {
        _db = db;
        _changeRepo = changeRepo;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetRecent(
        [FromQuery] string? changeType,
        [FromQuery] string? orgSid,
        [FromQuery] string? userHandle,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var query = _db.ChangeEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(changeType))
            query = query.Where(c => c.ChangeType == changeType);
        if (!string.IsNullOrWhiteSpace(orgSid))
            query = query.Where(c => c.OrgSid == orgSid);
        if (!string.IsNullOrWhiteSpace(userHandle))
            query = query.Where(c => c.UserHandle == userHandle);

        var changes = await query
            .OrderByDescending(c => c.Timestamp)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(changes.Select(MapChange).ToList());
    }

    [HttpGet("summary")]
    public async Task<ActionResult<IReadOnlyList<ChangeSummaryDto>>> GetSummary(
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var summary = await _db.ChangeEvents
            .Where(c => c.Timestamp >= since)
            .GroupBy(c => c.ChangeType)
            .Select(g => new ChangeSummaryDto { ChangeType = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync(ct);
        return Ok(summary);
    }

    [HttpGet("organizations/{sid}")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetByOrg(
        string sid,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByOrgSidAsync(sid.ToUpperInvariant(), limit, ct);
        return Ok(changes.Select(MapChange).ToList());
    }

    [HttpGet("types/{changeType}")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetByType(
        string changeType,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _db.ChangeEvents
            .Where(c => c.ChangeType == changeType)
            .OrderByDescending(c => c.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
        return Ok(changes.Select(MapChange).ToList());
    }

    private static ChangeEventDto MapChange(Collector.Models.ChangeEvent e) => new()
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
