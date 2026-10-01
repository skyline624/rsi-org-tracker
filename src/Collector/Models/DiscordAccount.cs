namespace Collector.Models;

/// <summary>A Discord account, shared by every tracked server it appears on.</summary>
public class DiscordAccount
{
    public long Id { get; set; }

    public string DiscordUserId { get; set; } = null!;

    public string Username { get; set; } = null!;

    /// <summary>Display name chosen by the user, null when unset.</summary>
    public string? GlobalName { get; set; }

    public bool IsBot { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
