using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class UserEnrichmentQueueRepository : Repository<UserEnrichmentQueue>, IUserEnrichmentQueueRepository
{
    /// <summary>A profile without a citizen record is checked again after this delay.</summary>
    public static readonly TimeSpan NoCitizenRecordRetry = TimeSpan.FromDays(14);

    public UserEnrichmentQueueRepository(TrackerDbContext context) : base(context) { }

    /// <summary>Delay before attempt N+1 after N failures: 1 h, 4 h, 16 h, then daily.</summary>
    public static TimeSpan RetryDelay(int failures)
        => TimeSpan.FromHours(Math.Min(Math.Pow(4, Math.Max(failures, 1) - 1), 24));

    /// <summary>Queue priorities, highest first: 1 for members new to an org, 0 for the rest.</summary>
    private static readonly int[] PrioritiesHighFirst = [1, 0];

    /// <summary>
    /// Due rows of one priority, oldest first. With Enriched and Priority as equalities the
    /// (Enriched, Priority, QueuedAt) index returns them in order; a single query ordered
    /// by Priority DESC, QueuedAt sorted every pending row (150-330 ms per batch on the
    /// production copy).
    /// </summary>
    public static IQueryable<UserEnrichmentQueue> DueQuery(IQueryable<UserEnrichmentQueue> source, int priority, DateTime now)
    {
        // A parameter, not the constant false: EF writes "== false" as NOT (Enriched),
        // which the index cannot seek on.
        var pending = false;
        return source
            .Where(q => q.Enriched == pending && q.Priority == priority
                && (q.NextAttemptAt == null || q.NextAttemptAt <= now))
            .OrderBy(q => q.QueuedAt);
    }

    public async Task<IReadOnlyList<UserEnrichmentQueue>> GetPendingAsync(int limit, DateTime now, CancellationToken ct = default)
    {
        var rows = new List<UserEnrichmentQueue>(limit);
        foreach (var priority in PrioritiesHighFirst)
        {
            if (rows.Count >= limit) break;
            rows.AddRange(await DueQuery(DbSet, priority, now).Take(limit - rows.Count).ToListAsync(ct));
        }
        return rows;
    }

    public async Task<int> CountPendingAsync(DateTime now, CancellationToken ct = default)
    {
        return await DbSet
            .AsNoTracking()
            .Where(q => !q.Enriched && (q.NextAttemptAt == null || q.NextAttemptAt <= now))
            .CountAsync(ct);
    }

    public Task MarkEnrichedAsync(long id, DateTime now, CancellationToken ct = default)
        => SettleAsync(id, EnrichmentOutcome.Enriched, error: null, now, ct);

    public Task MarkGoneAsync(long id, string? reason, DateTime now, CancellationToken ct = default)
        => SettleAsync(id, EnrichmentOutcome.Gone, reason, now, ct);

    public async Task DeferAsync(long id, string? reason, DateTime now, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item == null) return;

        // Not a failure: AttemptCount is untouched so it never leads to abandonment.
        item.Outcome = EnrichmentOutcome.NoCitizenRecord;
        item.LastError = reason;
        item.NextAttemptAt = now + NoCitizenRecordRetry;
        item.Priority = 0;
        await Context.SaveChangesAsync(ct);
    }

    public async Task RecordFailureAsync(long id, string? error, int maxAttempts, DateTime now, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item == null) return;

        item.AttemptCount++;
        item.LastError = error;
        if (item.AttemptCount >= maxAttempts)
        {
            item.Enriched = true;
            item.EnrichedAt = now;
            item.Outcome = EnrichmentOutcome.Abandoned;
        }
        else
        {
            item.Outcome = EnrichmentOutcome.Failed;
            item.NextAttemptAt = now + RetryDelay(item.AttemptCount);
        }
        await Context.SaveChangesAsync(ct);
    }

    /// <summary>Terminal outcome: the row leaves the pending set, freeing its handle.</summary>
    private async Task SettleAsync(long id, string outcome, string? error, DateTime now, CancellationToken ct)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item == null) return;

        item.Enriched = true;
        item.EnrichedAt = now;
        item.Outcome = outcome;
        item.LastError = error;
        await Context.SaveChangesAsync(ct);
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

    public async Task<IReadOnlyList<string>> GetRecentlySettledHandlesInAsync(
        IReadOnlyCollection<string> handles, DateTime since, CancellationToken ct = default)
    {
        if (handles.Count == 0) return Array.Empty<string>();
        return await DbSet
            .AsNoTracking()
            .Where(q => q.Enriched
                && (q.Outcome == EnrichmentOutcome.Gone || q.Outcome == EnrichmentOutcome.Abandoned)
                && q.EnrichedAt >= since
                && handles.Contains(q.UserHandle))
            .Select(q => q.UserHandle)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<int> InsertPendingIgnoreDuplicatesAsync(
        IReadOnlyList<UserEnrichmentQueue> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return 0;

        // One transaction for the batch instead of one commit per row. "INSERT OR
        // IGNORE" cooperates with the partial unique index
        // (IX_user_enrichment_queue_UserHandle_Pending) to skip any handle that
        // already has a pending row, without failing the batch.
        await using var transaction = await Context.Database.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var item in items)
        {
            inserted += await Context.Database.ExecuteSqlRawAsync(
                @"INSERT OR IGNORE INTO user_enrichment_queue
                    (UserHandle, Priority, Enriched, QueuedAt, AttemptCount, LastError, EnrichedAt)
                  VALUES ({0}, {1}, 0, {2}, 0, NULL, NULL);",
                new object[] { item.UserHandle, item.Priority, item.QueuedAt },
                ct);
        }
        await transaction.CommitAsync(ct);
        return inserted;
    }
}
