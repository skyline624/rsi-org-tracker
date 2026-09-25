using Collector.Models;

namespace Collector.Data.Repositories;

/// <summary>
/// The Phase 4 queue. A row is pending (Enriched = 0) until it reaches a terminal
/// outcome: enriched, gone or abandoned (Enriched = 1, see <see cref="EnrichmentOutcome"/>).
/// A pending row is due once its NextAttemptAt has passed.
/// </summary>
public interface IUserEnrichmentQueueRepository : IRepository<UserEnrichmentQueue>
{
    /// <summary>Due rows, highest priority then oldest first.</summary>
    Task<IReadOnlyList<UserEnrichmentQueue>> GetPendingAsync(int limit, DateTime now, CancellationToken ct = default);

    /// <summary>Number of due rows; Phase4Worker uses it to choose between idling and draining.</summary>
    Task<int> CountPendingAsync(DateTime now, CancellationToken ct = default);

    Task MarkEnrichedAsync(long id, DateTime now, CancellationToken ct = default);

    /// <summary>The profile answered 404: stop, and free the handle for a later queueing.</summary>
    Task MarkGoneAsync(long id, string? reason, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Live profile without a citizen record ("n/a"): checked again after
    /// <see cref="UserEnrichmentQueueRepository.NoCitizenRecordRetry"/>, without spending an attempt.
    /// </summary>
    Task DeferAsync(long id, string? reason, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Transient failure: retried after a growing delay, abandoned at
    /// <paramref name="maxAttempts"/> attempts.
    /// </summary>
    Task RecordFailureAsync(long id, string? error, int maxAttempts, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// The subset of <paramref name="handles"/> that currently have a pending row.
    /// Collectors use it to skip handles already queued.
    /// </summary>
    Task<IReadOnlyList<string>> GetPendingHandlesInAsync(IReadOnlyList<string> handles, CancellationToken ct = default);

    /// <summary>
    /// The subset of <paramref name="handles"/> that went gone or abandoned since
    /// <paramref name="since"/>: queueing them again would only repeat that outcome.
    /// </summary>
    Task<IReadOnlyList<string>> GetRecentlySettledHandlesInAsync(
        IReadOnlyCollection<string> handles, DateTime since, CancellationToken ct = default);

    /// <summary>
    /// Inserts queue entries in one transaction, silently skipping any handle that
    /// already has a pending row (partial unique index). Returns the rows written.
    /// </summary>
    Task<int> InsertPendingIgnoreDuplicatesAsync(IReadOnlyList<UserEnrichmentQueue> items, CancellationToken ct = default);
}
