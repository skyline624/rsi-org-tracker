using Collector.Models;

namespace Collector.Data.Repositories;

public interface IUserEnrichmentQueueRepository : IRepository<UserEnrichmentQueue>
{
    Task<IReadOnlyList<UserEnrichmentQueue>> GetPendingAsync(int limit = 100, int maxAttempts = int.MaxValue, CancellationToken ct = default);
    Task MarkEnrichedAsync(long id, CancellationToken ct = default);
    Task IncrementAttemptAsync(long id, string? error, CancellationToken ct = default);
    Task<bool> IsQueuedAsync(string userHandle, CancellationToken ct = default);

    /// <summary>
    /// Counts entries with Enriched=0 and AttemptCount &lt; <paramref name="maxAttempts"/>.
    /// Used by Phase4Worker to decide between idle and drain.
    /// </summary>
    Task<int> CountPendingAsync(int maxAttempts = int.MaxValue, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="handles"/> that currently have a row
    /// with Enriched=0. Used by collectors to skip enqueueing handles that are
    /// already pending (avoids redundant INSERT OR IGNORE round-trips).
    /// </summary>
    Task<IReadOnlyList<string>> GetPendingHandlesInAsync(IReadOnlyList<string> handles, CancellationToken ct = default);

    /// <summary>
    /// Inserts queue entries, silently skipping any handle that already has an active
    /// pending row (thanks to the partial unique index). Returns the number of rows
    /// actually written. Safe against concurrent inserters.
    /// </summary>
    Task<int> InsertPendingIgnoreDuplicatesAsync(IReadOnlyList<UserEnrichmentQueue> items, CancellationToken ct = default);
}