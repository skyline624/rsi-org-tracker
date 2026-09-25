using Collector.Api.Data;
using Collector.Data;
using Collector.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

[ApiController]
// Probes (systemd health checks, deploy script) must never be throttled.
[DisableRateLimiting]
public class HealthController : ControllerBase
{
    private static readonly DateTime _startTime = DateTime.UtcNow;
    private readonly ApiDbContext _apiDb;
    private readonly TrackerDbContext _trackerDb;
    private readonly ILogger<HealthController> _logger;

    public HealthController(ApiDbContext apiDb, TrackerDbContext trackerDb, ILogger<HealthController> logger)
    {
        _apiDb = apiDb;
        _trackerDb = trackerDb;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult Root() => Ok(new { status = "ok" });

    [AllowAnonymous]
    [HttpGet("api/health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [HttpGet("api/health/detailed")]
    public async Task<IActionResult> Detailed(CancellationToken ct)
    {
        var apiDbOk = await TryPingAsync(_apiDb, "api", ct);
        var trackerDbOk = await TryPingAsync(_trackerDb, "tracker", ct);

        return Ok(new
        {
            status = apiDbOk && trackerDbOk ? "ok" : "degraded",
            version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "unknown",
            uptime = (DateTime.UtcNow - _startTime).ToString(),
            databases = new { api = apiDbOk ? "ok" : "error", tracker = trackerDbOk ? "ok" : "error" },
        });
    }

    [AllowAnonymous]
    [HttpGet("api/health/live")]
    public IActionResult Live() => Ok(new { status = "alive" });

    [AllowAnonymous]
    [HttpGet("api/health/ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        if (!await TryPingAsync(_trackerDb, "tracker", ct))
            return StatusCode(503, new { status = "not ready" });

        // The collector migrates tracker.db. Until it has, this release's queries hit
        // missing columns: deploy.sh rolls back on this answer.
        try
        {
            var pending = (await _trackerDb.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0) return Ok(new { status = "ready" });
            _logger.LogWarning("tracker.db has pending migrations (restart the collector): {Migrations}",
                string.Join(", ", pending));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the tracker.db migration history");
        }
        return StatusCode(503, new { status = "not ready" });
    }

    /// <summary>
    /// Pings a database and logs the failure instead of silently swallowing it. Health
    /// endpoints still return a clean "degraded" status so we don't expose stack traces,
    /// but the operator sees the reason in the structured log.
    /// </summary>
    private async Task<bool> TryPingAsync(Microsoft.EntityFrameworkCore.DbContext db, string name, CancellationToken ct)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check: failed to connect to {Database}", name);
            return false;
        }
    }

    [HttpGet("api/health/cycle")]
    public async Task<IActionResult> CycleStatus(CancellationToken ct)
    {
        // Pending rows that are due now (an "n/a" profile or a failed fetch waits for
        // its next attempt), and rows given up after repeated failures in the last day.
        var now = DateTime.UtcNow;
        var queuePending = await _trackerDb.UserEnrichmentQueue
            .CountAsync(q => !q.Enriched && (q.NextAttemptAt == null || q.NextAttemptAt <= now), ct);

        var abandonedSince = now.AddDays(-1);
        var queueStuck = await _trackerDb.UserEnrichmentQueue
            .CountAsync(q => q.Outcome == EnrichmentOutcome.Abandoned && q.EnrichedAt >= abandonedSince, ct);

        // Phase 3 stamps each org it visits; sorting the 100k discovered orgs replaces
        // a scan of member_collection_log (31 M rows, no index on CollectionTime alone).
        var lastCollection = await _trackerDb.DiscoveredOrganizations
            .AsNoTracking()
            .Where(d => d.LastMembersCollectedAt != null)
            .OrderByDescending(d => d.LastMembersCollectedAt)
            .Select(d => new { OrgSid = d.Sid, CollectionTime = d.LastMembersCollectedAt!.Value })
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
