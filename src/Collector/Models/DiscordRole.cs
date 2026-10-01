namespace Collector.Models;

/// <summary>
/// A role of a tracked Discord server. Roles stand in for ranks: <see cref="IsRank"/>,
/// <see cref="RankOrder"/> and <see cref="RsiRankLabel"/> are set on the site, never by a sync.
/// </summary>
public class DiscordRole
{
    public long Id { get; set; }

    public string GuildId { get; set; } = null!;

    public string RoleId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Discord position: a higher role is listed above.</summary>
    public int Position { get; set; }

    /// <summary>"#rrggbb", null when the role has no colour.</summary>
    public string? Color { get; set; }

    /// <summary>Displayed separately in the member list.</summary>
    public bool Hoist { get; set; }

    /// <summary>Owned by an integration (bot role), never assigned by hand.</summary>
    public bool Managed { get; set; }

    /// <summary>Whether the role is a rank. On insert: Hoist and not Managed; afterwards changed on the site only.</summary>
    public bool IsRank { get; set; }

    /// <summary>Rank precedence, higher first. Non-null for every rank role: Position when the role becomes a rank.</summary>
    public int? RankOrder { get; set; }

    /// <summary>Equivalent RSI rank, compared with the RSI roster.</summary>
    public string? RsiRankLabel { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>Set when a sync no longer lists the role (the role list is always sent whole); cleared if it comes back.</summary>
    public DateTime? DeletedAt { get; set; }
}
