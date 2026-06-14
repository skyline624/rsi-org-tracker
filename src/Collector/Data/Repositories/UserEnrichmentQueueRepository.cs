using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class UserEnrichmentQueueRepository : Repository<UserEnrichmentQueue>, IUserEnrichmentQueueRepository
{
    public UserEnrichmentQueueRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<UserEnrichmentQueue>> GetPendingAsync(int limit = 100, int maxAttempts = int.MaxValue, CancellationToken ct = default)
    {
        return await DbSet
            .Where(q => !q.Enriched && q.AttemptCount < maxAttempts)
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.QueuedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task MarkEnrichedAsync(long id, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            item.Enriched = true;
            item.EnrichedAt = DateTime.UtcNow;
            await Context.SaveChangesAsync(ct);
        }
    }

    public async Task IncrementAttemptAsync(long id, string? error, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            item.AttemptCount++;
            item.LastError = error;
            await Context.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Sentinel attempt count used to park a "gone" (404) handle. Any real
    /// MaxEnrichmentAttempts threshold is far below this, so the AttemptCount &lt;
    /// maxAttempts filter in <see cref="GetPendingAsync"/> excludes it permanently.
    /// </summary>
    public const int GoneAttemptSentinel = int.MaxValue;

    public async Task MarkGoneAsync(long id, string? reason, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            item.AttemptCount = GoneAttemptSentinel;
            item.LastError = reason;
            await Context.SaveChangesAsync(ct);
        }
    }

    public async Task DeferAsync(long id, string? reason, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            // Push to the back of the queue (newest QueuedAt sorts last within a
            // priority) and drop priority so it never jumps ahead of unseen handles.
            // Crucially: do NOT touch AttemptCount — n/a is not a failure, so it must
            // never accumulate towards the abandon cap.
            item.QueuedAt = DateTime.UtcNow;
            item.Priority = 0;
            item.LastError = reason;
            await Context.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> IsQueuedAsync(string userHandle, CancellationToken ct = default)
    {
        return await DbSet.AnyAsync(q => q.UserHandle == userHandle && !q.Enriched, ct);
    }

    public async Task<int> CountPendingAsync(int maxAttempts = int.MaxValue, CancellationToken ct = default)
    {
        return await DbSet
            .AsNoTracking()
            .Where(q => !q.Enriched && q.AttemptCount < maxAttempts)
            .CountAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetPendingHandlesInAsync(IReadOnlyList<string> handles, CancellationToken ct = default)
    {
        if (handles.Count == 0) return Array.Empty<string>();
        return await DbSet
            .AsNoTracking()
            .Where(q => !q.Enriched && handles.Contains(q.UserHandle))
            .Select(q => q.UserHandle)
            .ToListAsync(ct);
    }

    public async Task<int> InsertPendingIgnoreDuplicatesAsync(
        IReadOnlyList<UserEnrichmentQueue> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return 0;

        // SQLite's "INSERT OR IGNORE" cooperates with the partial unique index
        // (IX_user_enrichment_queue_UserHandle_Pending) to atomically skip any
        // handle that already has an Enriched=0 row, avoiding the check/insert
        // race condition that would otherwise tear down the surrounding transaction.
        var inserted = 0;
        foreach (var item in items)
        {
            var rows = await Context.Database.ExecuteSqlRawAsync(
                @"INSERT OR IGNORE INTO user_enrichment_queue
                    (UserHandle, Priority, Enriched, QueuedAt, AttemptCount, LastError, EnrichedAt)
                  VALUES ({0}, {1}, 0, {2}, 0, NULL, NULL);",
                new object[] { item.UserHandle, item.Priority, item.QueuedAt },
                ct);
            inserted += rows;
        }
        return inserted;
    }
}