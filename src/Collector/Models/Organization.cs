namespace Collector.Models;

/// <summary>
/// Represents a Star Citizen organization with its metadata.
/// Multiple records per organization track changes over time.
/// </summary>
public class Organization
{
    public long Id { get; set; }

    /// <summary>
    /// Organization symbol/identifier (e.g., "TEST", "ARC").
    /// </summary>
    public string Sid { get; set; } = null!;

    /// <summary>
    /// Timestamp when this snapshot was collected.
    /// </summary>
    public DateTime Timestamp { get; set; }

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
    /// Organization archetype (e.g., "Organization", "Squadron").
    /// </summary>
    public string? Archetype { get; set; }

    /// <summary>
    /// Primary language of the organization.
    /// </summary>
    public string? Lang { get; set; }

    /// <summary>
    /// Commitment level (e.g., "Casual", "Hardcore").
    /// </summary>
    public string? Commitment { get; set; }

    /// <summary>
    /// Whether the organization is recruiting.
    /// </summary>
    public bool? Recruiting { get; set; }

    /// <summary>
    /// Whether the organization engages in roleplay.
    /// </summary>
    public bool? Roleplay { get; set; }

    /// <summary>
    /// Current member count.
    /// </summary>
    public int MembersCount { get; set; }
}