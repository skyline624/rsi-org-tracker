using Collector.Api.Extensions;
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
        limit = Paging.Limit(limit);
        var query = _db.ChangeEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(changeType))
            query = query.Where(c => c.ChangeType == changeType);
        if (!string.IsNullOrWhiteSpace(orgSid))
            query = query.Where(c => c.OrgSid == orgSid);
        if (!string.IsNullOrWhiteSpace(userHandle))
            query = query.Where(c => c.UserHandle == userHandle);

        // Newest recorded first: the rowid order needs no sort (ORDER BY Timestamp scanned
        // and sorted every event), and with a type filter the ChangeType index is
        // already in rowid order.
        var changes = await query
            .OrderByDescending(c => c.Id)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(changes.Select(MapChange).ToList());
    }

    [HttpGet("summary")]
    public async Task<ActionResult<IReadOnlyList<ChangeSummaryDto>>> GetSummary(
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Paging.Days(days));
        var summary = await _db.ChangeEvents
            .Where(c => c.Timestamp >= since)
            .GroupBy(c => c.ChangeType)
            .Select(g => new ChangeSummaryDto { ChangeType = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToListAsync(ct);
        return Ok(summary);
    }

    [HttpGet("organizations/{sid}")]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetByOrg(
        string sid,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _changeRepo.GetByOrgSidAsync(sid.ToUpperInvariant(), Paging.Limit(limit), ct);
        return Ok(changes.Select(MapChange).ToList());
    }

    [HttpGet("types/{changeType}")]
    public async Task<ActionResult<IReadOnlyList<ChangeEventDto>>> GetByType(
        string changeType,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var changes = await _db.ChangeEvents
            .AsNoTracking()
            .Where(c => c.ChangeType == changeType)
            .OrderByDescending(c => c.Id)
            .Take(Paging.Limit(limit))
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
