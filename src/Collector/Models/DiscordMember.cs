namespace Collector.Models;

/// <summary>
/// Current state of one account on one server: a single row per (server, account),
/// kept after a departure (<see cref="LeftAt"/>). Past stays are rebuilt from the event log.
/// </summary>
public class DiscordMember
{
    public long Id { get; set; }

    public string GuildId { get; set; } = null!;

    public string DiscordUserId { get; set; } = null!;

    /// <summary>Server nickname, null when unset.</summary>
    public string? Nick { get; set; }

    /// <summary>JSON array of role ids (strings), sorted ordinal.</summary>
    public string RoleIdsJson { get; set; } = "[]";

    /// <summary>Discord join date, truncated to the second; null when never received.</summary>
    public DateTime? JoinedAt { get; set; }

    /// <summary>CollectedAt of the first sync that listed the member.</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>CollectedAt of the latest sync that listed the member.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>Null while the member is present; the complete sync's CollectedAt after a departure.</summary>
    public DateTime? LeftAt { get; set; }
}
