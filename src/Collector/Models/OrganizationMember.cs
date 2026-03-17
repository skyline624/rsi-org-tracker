namespace Collector.Models;

/// <summary>
/// Represents a member of an organization at a specific point in time.
/// Multiple records per member track rank changes and membership history.
/// </summary>
public class OrganizationMember
{
    public long Id { get; set; }

    /// <summary>
    /// Organization symbol this member belongs to.
    /// </summary>
    public string OrgSid { get; set; } = null!;

    /// <summary>
    /// User's RSI handle (can change over time).
    /// </summary>
    public string UserHandle { get; set; } = null!;

    /// <summary>
    /// Permanent RSI citizen ID (primary identifier for change detection).
    /// </summary>
    public int? CitizenId { get; set; }

    /// <summary>
    /// Timestamp when this snapshot was collected.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// User's display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Member's rank within the organization.
    /// </summary>
    public string? Rank { get; set; }

    /// <summary>
    /// Member's roles as JSON array (e.g., ["Founder", "Officer"]).
    /// </summary>
    public string? RolesJson { get; set; }

    /// <summary>
    /// URL to member's avatar image.
    /// </summary>
    public string? UrlImage { get; set; }

    /// <summary>
    /// Whether this member is currently active in the organization.
    /// Set to false when the member is no longer seen in a collection cycle.
    /// </summary>
    public bool IsActive { get; set; } = true;
}