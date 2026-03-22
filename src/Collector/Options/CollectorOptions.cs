namespace Collector.Options;

/// <summary>
/// Configuration options for the collector.
/// </summary>
public class CollectorOptions
{
    /// <summary>
    /// Interval between complete collection cycles.
    /// </summary>
    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Delay after an error before retrying.
    /// </summary>
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Base delay between API requests (rate limiting).
    /// </summary>
    public double RateLimitDelaySeconds { get; set; } = 0.5;

    /// <summary>
    /// Maximum retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Batch size for bulk database operations.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum concurrent API requests.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 5;

    /// <summary>
    /// Sort methods to use for organization discovery.
    /// </summary>
    public string[] DiscoverSortMethods { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Page size for member collection (max 32).
    /// </summary>
    public int MemberCollectionPageSize { get; set; } = 32;

    /// <summary>
    /// Page size for organization discovery (max ~12).
    /// </summary>
    public int OrganizationPageSize { get; set; } = 12;

    /// <summary>
    /// Number of empty pages before stopping discovery.
    /// </summary>
    public int EmptyPagesThreshold { get; set; } = 5;

    /// <summary>
    /// Maximum pages to fetch per sort method during discovery (0 = unlimited).
    /// </summary>
    public int DiscoveryMaxPages { get; set; } = 0;

    /// <summary>
    /// How many hours before an org's extended content (description/history/manifesto/charter) is considered stale
    /// and eligible for re-collection in Phase 2. Default: 168h (7 days).
    /// </summary>
    public int MetadataRefreshIntervalHours { get; set; } = 168;

    /// <summary>
    /// Maximum number of enrichment attempts per user handle before giving up.
    /// Handles exceeding this limit are skipped in Phase 4 (deleted/banned accounts).
    /// </summary>
    public int MaxEnrichmentAttempts { get; set; } = 3;
}