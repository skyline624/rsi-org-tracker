using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

public interface IEnrichmentBackfillService
{
    Task<int> BackfillOrphansAsync(CancellationToken ct = default);
}

/// <summary>
/// Finds handles that are active members of some org but absent from the users
/// table, and enqueues them in user_enrichment_queue. One-shot repair that
/// complements the per-cycle orphan-rescue branch in MemberCollector.
/// </summary>
public class EnrichmentBackfillService : IEnrichmentBackfillService
{
    private readonly TrackerDbContext _db;
    private readonly IUserEnrichmentQueueRepository _queueRepo;
    private readonly ILogger<EnrichmentBackfillService> _logger;

    public EnrichmentBackfillService(
        TrackerDbContext db,
        IUserEnrichmentQueueRepository queueRepo,
        ILogger<EnrichmentBackfillService> logger)
    {
        _db = db;
        _queueRepo = queueRepo;
        _logger = logger;
    }

    public async Task<int> BackfillOrphansAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Scanning for orphan handles (active members without a users row)");

        var orphans = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.IsActive)
            .Select(m => m.UserHandle)
            .Distinct()
            .Where(h => !_db.Users.Any(u => u.UserHandle == h))
            .ToListAsync(ct);

        if (orphans.Count == 0)
        {
            _logger.LogInformation("Backfill: no orphan handles found");
            return 0;
        }

        var now = DateTime.UtcNow;
        var items = orphans
            .Select(h => new UserEnrichmentQueue
            {
                UserHandle = h,
                Priority = 0,
                Enriched = false,
                QueuedAt = now
            })
            .ToList();

        var inserted = await _queueRepo.InsertPendingIgnoreDuplicatesAsync(items, ct);
        _logger.LogInformation(
            "Backfill complete: {Orphans} orphan handles detected, {Inserted} newly queued (rest already pending)",
            orphans.Count, inserted);
        return inserted;
    }
}
