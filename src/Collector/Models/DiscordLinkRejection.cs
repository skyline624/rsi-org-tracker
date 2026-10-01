namespace Collector.Models;

/// <summary>A link suggestion (Discord account to RSI citizen) that a user dismissed: it is not suggested again.</summary>
public class DiscordLinkRejection
{
    public long Id { get; set; }

    public string DiscordUserId { get; set; } = null!;

    /// <summary>The CitizenId as a decimal string, or "h:" followed by the lower-case handle when it is unknown.</summary>
    public string CitizenKey { get; set; } = null!;

    public long ByApiUserId { get; set; }

    public string ByUsername { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
