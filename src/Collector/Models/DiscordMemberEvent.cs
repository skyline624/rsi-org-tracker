namespace Collector.Models;

/// <summary>
/// One entry of the Discord history. Ids give the feed order (newest Id first, as for
/// <see cref="ChangeEvent"/>). Account-level changes carry a null <see cref="GuildId"/>.
/// </summary>
public class DiscordMemberEvent
{
    public long Id { get; set; }

    /// <summary>Server of the event; null for username and global-name changes.</summary>
    public string? GuildId { get; set; }

    public string DiscordUserId { get; set; } = null!;

    /// <summary>The discord_syncs row that produced the event, hence its submitter.</summary>
    public long SyncId { get; set; }

    /// <summary>See <see cref="DiscordEventTypes"/>.</summary>
    public string Type { get; set; } = null!;

    /// <summary>Previous value: a JoinedAt (ISO 8601 UTC), a name, or roles as JSON [{id,name}].</summary>
    public string? OldValue { get; set; }

    /// <summary>New value, same formats as <see cref="OldValue"/>.</summary>
    public string? NewValue { get; set; }

    /// <summary>Exact date when known (a Discord join date).</summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>Earliest possible date when the exact one is unknown: the previous observation.</summary>
    public DateTime? NotBefore { get; set; }

    /// <summary>CollectedAt of the sync that saw the change.</summary>
    public DateTime ObservedAt { get; set; }
}

/// <summary>Values of <see cref="DiscordMemberEvent.Type"/>.</summary>
public static class DiscordEventTypes
{
    public const string Joined = "joined", Left = "left", Rejoined = "rejoined",
        RolesChanged = "roles_changed", NickChanged = "nick_changed",
        UsernameChanged = "username_changed", GlobalNameChanged = "global_name_changed";
}
