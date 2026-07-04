namespace Collector.Models;

/// <summary>
/// A manually-recorded membership linking a tracked person (<see cref="TrackedEntity"/>)
/// to an organization (by SID). Lives in its own table, never touched by the collector,
/// so it survives roster re-collections. Used notably for people known only through an
/// org's Discord and not listed on its RSI page.
/// </summary>
public class EntityMembership
{
    public long Id { get; set; }

    /// <summary>The tracked person.</summary>
    public long TrackedEntityId { get; set; }

    /// <summary>Organization SID this person is attached to.</summary>
    public string OrgSid { get; set; } = null!;

    /// <summary>Optional free-text rank / role (e.g. "Officier").</summary>
    public string? Rank { get; set; }

    /// <summary>Where the membership is known from. See <see cref="MembershipVia"/>.</summary>
    public string Via { get; set; } = MembershipVia.Discord;

    /// <summary>Member-since date; defaults to the day the link was recorded.</summary>
    public DateTime SinceDate { get; set; }

    /// <summary>ApiUser id of the author (soft reference into api.db — no cross-db FK).</summary>
    public long AuthorApiUserId { get; set; }

    /// <summary>Author username, denormalized for display.</summary>
    public string AuthorUsername { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}

/// <summary>Where a manual membership is observed from.</summary>
public static class MembershipVia
{
    public const string Rsi = "rsi";
    public const string Discord = "discord";
    public const string Both = "both";

    public static bool IsValid(string v) => v is Rsi or Discord or Both;
}
