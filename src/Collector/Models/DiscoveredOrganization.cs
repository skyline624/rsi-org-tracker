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
}