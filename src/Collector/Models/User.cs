namespace Collector.Models;

/// <summary>
/// Enriched user profile collected during Phase 4.
/// Contains detailed information from RSI profile page scraping.
/// </summary>
public class User
{
    public long Id { get; set; }

    /// <summary>
    /// Permanent RSI citizen ID.
    /// </summary>
    public int CitizenId { get; set; }

    /// <summary>
    /// Current RSI handle.
    /// </summary>
    public string UserHandle { get; set; } = null!;

    /// <summary>
    /// Display name on RSI profile.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// URL to user's avatar.
    /// </summary>
    public string? UrlImage { get; set; }

    /// <summary>
    /// User's biography/flavor text.
    /// </summary>
    public string? Bio { get; set; }

    /// <summary>
    /// Location/region information.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Enlisted date (account creation).
    /// </summary>
    public DateTime? Enlisted { get; set; }

    /// <summary>
    /// Record creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}