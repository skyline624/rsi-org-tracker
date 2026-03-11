namespace Collector.Models;

/// <summary>
/// Queue of users that need their profiles enriched (Phase 4).
/// </summary>
public class UserEnrichmentQueue
{
    public long Id { get; set; }

    /// <summary>
    /// User handle to enrich.
    /// </summary>
    public string UserHandle { get; set; } = null!;

    /// <summary>
    /// Priority for enrichment (higher = more important).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether this user has been enriched.
    /// </summary>
    public bool Enriched { get; set; }

    /// <summary>
    /// When this user was added to the queue.
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// When this user was enriched (if completed).
    /// </summary>
    public DateTime? EnrichedAt { get; set; }

    /// <summary>
    /// Number of enrichment attempts.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Last error message (if any).
    /// </summary>
    public string? LastError { get; set; }
}