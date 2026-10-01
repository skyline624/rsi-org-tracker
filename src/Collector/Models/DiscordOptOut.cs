namespace Collector.Models;

/// <summary>A Discord account erased at its owner's request: syncs drop it on receipt.</summary>
public class DiscordOptOut
{
    public long Id { get; set; }

    public string DiscordUserId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>Admin who erased the account; null for the static admin key, which has no api_users row.</summary>
    public long? ByApiUserId { get; set; }

    public string ByUsername { get; set; } = null!;

    public string? Reason { get; set; }
}
