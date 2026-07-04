namespace Collector.Models;

/// <summary>
/// A link from a tracked person to their profile on another tool (UEX, Discord, …).
/// Keyed on the stable internal entity, so it works for enriched and redacted
/// people alike. At most one link per (entity, provider).
/// </summary>
public class EntityLink
{
    public long Id { get; set; }
    public long TrackedEntityId { get; set; }

    /// <summary>Provider slug: "uex", "discord", …</summary>
    public string Provider { get; set; } = null!;

    /// <summary>External identifier (UEX id, Discord user id, …).</summary>
    public string Value { get; set; } = null!;

    public long AuthorApiUserId { get; set; }
    public string AuthorUsername { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Known external-link providers. The clickable URL is built on the frontend.</summary>
public static class LinkProviders
{
    public const string Uex = "uex";
    public const string Discord = "discord";
    public const string Twitch = "twitch";

    public static bool IsValid(string provider) => provider is Uex or Discord or Twitch;
}
