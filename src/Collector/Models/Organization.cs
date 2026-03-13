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

    // ── Extended content (Phase 2) ─────────────────────────────────────────

    /// <summary>Short description/about text.</summary>
    public string? Description { get; set; }

    /// <summary>History section text.</summary>
    public string? History { get; set; }

    /// <summary>Manifesto section text.</summary>
    public string? Manifesto { get; set; }

    /// <summary>Charter section text.</summary>
    public string? Charter { get; set; }

    /// <summary>Primary focus area name.</summary>
    public string? FocusPrimaryName { get; set; }

    /// <summary>Primary focus area image URL.</summary>
    public string? FocusPrimaryImage { get; set; }

    /// <summary>Secondary focus area name.</summary>
    public string? FocusSecondaryName { get; set; }

    /// <summary>Secondary focus area image URL.</summary>
    public string? FocusSecondaryImage { get; set; }

    /// <summary>Whether extended content has been collected (Phase 2).</summary>
    public bool ContentCollected { get; set; }
}