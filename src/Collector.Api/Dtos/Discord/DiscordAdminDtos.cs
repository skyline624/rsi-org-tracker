namespace Collector.Api.Dtos.Discord;

/// <summary>A Discord account kept out of every sync, after an erasure or an opposition.</summary>
public sealed class DiscordOptOutDto
{
    public string DiscordUserId { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public string ByUsername { get; set; } = null!;
    public string? Reason { get; set; }
}

/// <summary>A guild whose syncs are refused (409 guild_excluded).</summary>
public sealed class DiscordGuildOptOutDto
{
    public string GuildId { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public string ByUsername { get; set; } = null!;
    public string? Reason { get; set; }
}
