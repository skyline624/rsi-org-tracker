namespace Collector.Models;

/// <summary>
/// A voice recording (uploaded audio file) attached to a <see cref="TrackedEntity"/>.
/// The file itself lives on disk (outside the DB); this row holds the metadata and a
/// path relative to the audio storage root.
/// </summary>
public class EntityAudio
{
    public long Id { get; set; }

    /// <summary>The tracked entity this recording is about.</summary>
    public long TrackedEntityId { get; set; }

    /// <summary>ApiUser id of the uploader (soft reference into api.db — no cross-db FK).</summary>
    public long AuthorApiUserId { get; set; }

    /// <summary>Uploader username, denormalized for display.</summary>
    public string AuthorUsername { get; set; } = null!;

    /// <summary>Original file name as uploaded (for display / download).</summary>
    public string OriginalName { get; set; } = null!;

    /// <summary>Path on disk, relative to the audio storage root.</summary>
    public string StoredPath { get; set; } = null!;

    /// <summary>MIME type (e.g. audio/mpeg).</summary>
    public string MimeType { get; set; } = null!;

    public long SizeBytes { get; set; }

    /// <summary>Duration in seconds when known (not extracted server-side; may be null).</summary>
    public double? DurationSec { get; set; }

    public DateTime CreatedAt { get; set; }
}
