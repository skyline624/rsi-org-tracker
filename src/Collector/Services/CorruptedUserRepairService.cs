using System.Text.RegularExpressions;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

public interface ICorruptedUserRepairService
{
    Task<RepairReport> RepairAsync(CancellationToken ct = default);
}

public record RepairReport(int Scanned, int Repaired, int Unrecoverable, int Requeued);

/// <summary>
/// One-shot repair for users rows whose <c>UserHandle</c> was corrupted by the
/// "CITIZEN DOSSIER" parser regression. For every corrupted row we look up the
/// most recent URL-safe handle in <c>user_handle_history</c> for the same
/// <c>citizen_id</c>; failing that we delete the row and re-queue the original
/// handles (resolved via active member rows) for a fresh enrichment.
/// </summary>
public class CorruptedUserRepairService : ICorruptedUserRepairService
{
    // Same shape used by UserCollector + UserProfileHtmlParser. Any handle
    // outside this regex is treated as corrupted.
    private static readonly Regex HandleShape =
        new(@"^[A-Za-z0-9_-]{3,50}$", RegexOptions.Compiled);

    private readonly TrackerDbContext _db;
    private readonly IUserEnrichmentQueueRepository _queueRepo;
    private readonly ILogger<CorruptedUserRepairService> _logger;

    public CorruptedUserRepairService(
        TrackerDbContext db,
        IUserEnrichmentQueueRepository queueRepo,
        ILogger<CorruptedUserRepairService> logger)
    {
        _db = db;
        _queueRepo = queueRepo;
        _logger = logger;
    }

    public async Task<RepairReport> RepairAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Scanning users table for corrupted UserHandle values…");

        // Pull all users — the corrupted set is at most ~80k rows and we need
        // CPU-side regex anyway (LINQ-to-EF can't translate it).
        var allUsers = await _db.Users.AsNoTracking().ToListAsync(ct);
        var corrupted = allUsers
            .Where(u => !HandleShape.IsMatch(u.UserHandle))
            .ToList();

        _logger.LogInformation(
            "Found {Corrupted} corrupted rows out of {Total} users", corrupted.Count, allUsers.Count);

        var repaired = 0;
        var unrecoverable = 0;
        var orphanHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bad in corrupted)
        {
            ct.ThrowIfCancellationRequested();

            var cleanHandle = await ResolveCleanHandleAsync(bad.CitizenId, ct);
            if (cleanHandle is not null)
            {
                // Update via raw SQL so we don't fight the EF change tracker on a
                // tracked entity we loaded as NoTracking.
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE users SET UserHandle = {0}, UpdatedAt = {1} WHERE Id = {2};",
                    new object[] { cleanHandle, DateTime.UtcNow, bad.Id }, ct);
                repaired++;
                continue;
            }

            // No recoverable handle in history. Drop the row and collect any
            // currently-known member handles for re-enrichment.
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE FROM users WHERE Id = {0};", new object[] { bad.Id }, ct);
            unrecoverable++;

            var memberHandles = await _db.OrganizationMembers
                .AsNoTracking()
                .Where(m => m.CitizenId == bad.CitizenId && m.IsActive)
                .Select(m => m.UserHandle)
                .Distinct()
                .ToListAsync(ct);
            foreach (var h in memberHandles)
            {
                if (HandleShape.IsMatch(h)) orphanHandles.Add(h);
            }
        }

        // Re-queue the orphans so Phase 4 picks them up with the fixed parser.
        var now = DateTime.UtcNow;
        var requeued = await _queueRepo.InsertPendingIgnoreDuplicatesAsync(
            orphanHandles.Select(h => new UserEnrichmentQueue
            {
                UserHandle = h,
                Priority = 0,
                Enriched = false,
                QueuedAt = now
            }).ToList(),
            ct);

        _logger.LogInformation(
            "Repair complete: scanned={Scanned} repaired={Repaired} unrecoverable={Unrecoverable} requeued={Requeued}",
            corrupted.Count, repaired, unrecoverable, requeued);

        return new RepairReport(corrupted.Count, repaired, unrecoverable, requeued);
    }

    private async Task<string?> ResolveCleanHandleAsync(int citizenId, CancellationToken ct)
    {
        var historyHandles = await _db.UserHandleHistories
            .AsNoTracking()
            .Where(h => h.CitizenId == citizenId)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => h.UserHandle)
            .ToListAsync(ct);

        return historyHandles.FirstOrDefault(h => HandleShape.IsMatch(h));
    }
}
