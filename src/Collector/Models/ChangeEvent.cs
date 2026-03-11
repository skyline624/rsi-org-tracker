namespace Collector.Models;

/// <summary>
/// Represents a detected change (join, leave, rank change, etc.).
/// </summary>
public class ChangeEvent
{
    public long Id { get; set; }

    /// <summary>
    /// When this change was detected.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Type of entity that changed: "organization" or "member".
    /// </summary>
    public string EntityType { get; set; } = null!;

    /// <summary>
    /// Identifier for the entity (org_sid or user_handle).
    /// </summary>
    public string EntityId { get; set; } = null!;

    /// <summary>
    /// Type of change: "member_joined", "member_left", "rank_changed", etc.
    /// </summary>
    public string ChangeType { get; set; } = null!;

    /// <summary>
    /// Previous value (JSON serialized).
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    /// New value (JSON serialized).
    /// </summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// Organization SID (for member changes).
    /// </summary>
    public string? OrgSid { get; set; }

    /// <summary>
    /// User handle (for member changes).
    /// </summary>
    public string? UserHandle { get; set; }
}