namespace Collector.Models;

/// <summary>A Discord server deleted and excluded from tracking: its syncs are refused (409 guild_excluded).</summary>
public class DiscordGuildOptOut
{
    public long Id { get; set; }

    public string GuildId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>Admin who excluded the server; null for the static admin key.</summary>
    public long? ByApiUserId { get; set; }

    public string ByUsername { get; set; } = null!;

    public string? Reason { get; set; }
}
