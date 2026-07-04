namespace Collector.Models;

/// <summary>
/// A free-text note attached to a <see cref="TrackedEntity"/>, written by a tool
/// user. Notes are full-text indexed (entity_notes_fts) so that a search term
/// matching a note surfaces the person the note is attached to.
/// </summary>
public class EntityNote
{
    public long Id { get; set; }

    /// <summary>The tracked entity this note is about.</summary>
    public long TrackedEntityId { get; set; }

    /// <summary>ApiUser id of the author (soft reference into api.db — no cross-db FK).</summary>
    public long AuthorApiUserId { get; set; }

    /// <summary>Author username, denormalized for display without a cross-db join.</summary>
    public string AuthorUsername { get; set; } = null!;

    /// <summary>The note text.</summary>
    public string Body { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
