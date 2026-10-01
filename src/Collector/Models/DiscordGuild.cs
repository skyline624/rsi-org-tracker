namespace Collector.Models;

/// <summary>
/// A Discord server whose member list the Vencord plugin sends. Linked to at most one
/// RSI organization (<see cref="OrgSid"/>); an organization can have several servers.
/// Only the API writes the discord_* tables.
/// </summary>
public class DiscordGuild
{
    public long Id { get; set; }

    /// <summary>Discord snowflake, kept as text.</summary>
    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Icon hash ("a_" prefix when animated), null when the server has no icon.</summary>
    public string? IconHash { get; set; }

    /// <summary>Linked RSI organization (upper-case SID), null while the server is unmapped.</summary>
    public string? OrgSid { get; set; }

    /// <summary>ApiUser who linked the organization: the server's responsible user (soft reference into api.db).</summary>
    public long? OrgMappedByApiUserId { get; set; }

    /// <summary>That user's name, denormalized for display.</summary>
    public string? OrgMappedByUsername { get; set; }

    public DateTime? OrgMappedAt { get; set; }

    /// <summary>Member count Discord declared at the last sync.</summary>
    public int? MemberCount { get; set; }

    /// <summary>CollectedAt of the baseline sync: a member who joined earlier was there before tracking started.</summary>
    public DateTime FirstSyncAt { get; set; }

    /// <summary>ReceivedAt of the last accepted sync.</summary>
    public DateTime LastSyncAt { get; set; }

    /// <summary>CollectedAt of the last accepted sync.</summary>
    public DateTime LastCollectedAt { get; set; }

    /// <summary>CollectedAt of the last complete sync.</summary>
    public DateTime? LastCompleteSyncAt { get; set; }

    /// <summary>Legacy SQLite field retained for compatibility; it no longer affects ingestion.</summary>
    public bool AllowMassDepartureOnce { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
