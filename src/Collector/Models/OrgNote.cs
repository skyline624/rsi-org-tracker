namespace Collector.Models;

/// <summary>
/// A free-text note attached to an organization (by SID), written by a tool user.
/// Symmetric to <see cref="EntityNote"/> but keyed on the org's stable SID.
/// </summary>
public class OrgNote
{
    public long Id { get; set; }

    /// <summary>Organization SID this note is about.</summary>
    public string OrgSid { get; set; } = null!;

    /// <summary>ApiUser id of the author (soft reference into api.db — no cross-db FK).</summary>
    public long AuthorApiUserId { get; set; }

    /// <summary>Author username, denormalized for display.</summary>
    public string AuthorUsername { get; set; } = null!;

    public string Body { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
