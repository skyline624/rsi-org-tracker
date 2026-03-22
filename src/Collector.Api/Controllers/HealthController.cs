using Collector.Api.Data;
using Collector.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

[ApiController]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private static readonly DateTime _startTime = DateTime.UtcNow;
    private readonly ApiDbContext _apiDb;
    private readonly TrackerDbContext _trackerDb;

    public HealthController(ApiDbContext apiDb, TrackerDbContext trackerDb)
    {
        _apiDb = apiDb;
        _trackerDb = trackerDb;
    }

    [HttpGet("/")]
    public IActionResult Root() => Ok(new { status = "ok" });

    [HttpGet("api/health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [HttpGet("api/health/detailed")]
    public async Task<IActionResult> Detailed(CancellationToken ct)
    {
        bool apiDbOk, trackerDbOk;
        try { apiDbOk = await _apiDb.Database.CanConnectAsync(ct); } catch { apiDbOk = false; }
        try { trackerDbOk = await _trackerDb.Database.CanConnectAsync(ct); } catch { trackerDbOk = false; }

        return Ok(new
        {
            status = apiDbOk && trackerDbOk ? "ok" : "degraded",
            version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "unknown",
            uptime = (DateTime.UtcNow - _startTime).ToString(),
            databases = new { api = apiDbOk ? "ok" : "error", tracker = trackerDbOk ? "ok" : "error" },
        });
    }

    [HttpGet("api/health/live")]
    public IActionResult Live() => Ok(new { status = "alive" });

    [HttpGet("api/health/ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        try
        {
            await _trackerDb.Database.CanConnectAsync(ct);
            return Ok(new { status = "ready" });
        }
        catch
        {
            return StatusCode(503, new { status = "not ready" });
        }
    }

    [HttpGet("api/health/cycle")]
    public async Task<IActionResult> CycleStatus(CancellationToken ct)
    {
        var queuePending = await _trackerDb.UserEnrichmentQueue
            .CountAsync(q => !q.Enriched, ct);

        var queueStuck = await _trackerDb.UserEnrichmentQueue
            .CountAsync(q => !q.Enriched && q.AttemptCount >= 3, ct);

        var lastCollection = await _trackerDb.MemberCollectionLogs
            .OrderByDescending(l => l.CollectionTime)
            .Select(l => new { l.OrgSid, l.CollectionTime })
            .FirstOrDefaultAsync(ct);

        var orgCount = await _trackerDb.DiscoveredOrganizations
            .CountAsync(ct);

        return Ok(new
        {
            queue_pending = queuePending,
            queue_stuck = queueStuck,
            last_member_collection = lastCollection != null
                ? new { org_sid = lastCollection.OrgSid, at = lastCollection.CollectionTime }
                : null,
            discovered_orgs = orgCount,
        });
    }
}
