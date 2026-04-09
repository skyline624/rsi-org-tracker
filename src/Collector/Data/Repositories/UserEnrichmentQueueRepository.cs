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

    public async Task<bool> IsQueuedAsync(string userHandle, CancellationToken ct = default)
    {
        return await DbSet.AnyAsync(q => q.UserHandle == userHandle && !q.Enriched, ct);
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