namespace Collector.Models;

/// <summary>
/// Stable, tool-internal identity for a tracked person, decoupled from RSI's own
/// identifiers. A citizen's RSI handle can change over time, and "redacted" accounts
/// expose no CitizenId at all, so notes, audio and manual entries attach to this
/// entity rather than directly to a handle or CitizenId.
/// </summary>
public class TrackedEntity
{
    public long Id { get; set; }

    /// <summary>RSI citizen number when known; null for redacted / roster-only people.</summary>
    public int? CitizenId { get; set; }

    /// <summary>Most recently seen handle for this entity (may change over time).</summary>
    public string? CurrentHandle { get; set; }

    /// <summary>Display name when known.</summary>
    public string? DisplayName { get; set; }

    /// <summary>How this entity entered the system. See <see cref="TrackedEntitySource"/>.</summary>
    public string Source { get; set; } = TrackedEntitySource.Collected;

    /// <summary>Lifecycle status. See <see cref="TrackedEntityStatus"/>.</summary>
    public string Status { get; set; } = TrackedEntityStatus.Active;

    /// <summary>When Status = "merged", the surviving entity this one was folded into.</summary>
    public long? MergedIntoId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Origin of a <see cref="TrackedEntity"/>.</summary>
public static class TrackedEntitySource
{
    public const string Collected = "collected";
    public const string Manual = "manual";
}

/// <summary>Lifecycle status of a <see cref="TrackedEntity"/>.</summary>
public static class TrackedEntityStatus
{
    public const string Active = "active";
    public const string Redacted = "redacted";
    public const string Merged = "merged";
}
