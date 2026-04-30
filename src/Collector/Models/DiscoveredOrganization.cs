namespace Collector.Models;

/// <summary>
/// Organization discovered during Phase 1 (discovery).
/// Contains minimal information needed for Phase 2 (metadata collection).
/// </summary>
public class DiscoveredOrganization
{
    public long Id { get; set; }

    /// <summary>
    /// Organization symbol/identifier.
    /// </summary>
    public string Sid { get; set; } = null!;

    /// <summary>
    /// Organization name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// URL to organization logo/image.
    /// </summary>
    public string? UrlImage { get; set; }

    /// <summary>
    /// URL to organization page.
    /// </summary>
    public string? UrlCorpo { get; set; }

    /// <summary>
    /// When this organization was discovered.
    /// </summary>
    public DateTime DiscoveredAt { get; set; }

    /// <summary>
    /// Number of consecutive Phase 2 fetches that came back HTTP 404 from RSI.
    /// Reset to 0 on the next successful fetch.
    /// </summary>
    public int ConsecutiveNotFoundCount { get; set; }

    /// <summary>
    /// When the org crossed the "consecutive 404" threshold and was tombstoned.
    /// Non-null means Phase 2 will skip it from now on.
    /// </summary>
    public DateTime? DeadAt { get; set; }
}