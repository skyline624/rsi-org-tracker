namespace Collector.Api.Services.Discord;

/// <summary>
/// Machine-readable <c>code</c> values of the Discord ProblemDetails (CONTRACTS § 4). The
/// plugin switches on them, so they never change once shipped.
/// </summary>
public static class DiscordErrorCodes
{
    /// <summary>400: the body breaks a rule of spec § 7.3.</summary>
    public const string InvalidSync = "invalid_sync";

    /// <summary>400: <c>complete: true</c> with no member, which would make everyone leave.</summary>
    public const string EmptyCompleteSync = "empty_complete_sync";

    /// <summary>409: a sync of this guild was accepted after this collection started.</summary>
    public const string StaleSync = "stale_sync";

    /// <summary>409: the guild is in discord_guild_optouts.</summary>
    public const string GuildExcluded = "guild_excluded";

    /// <summary>503: the Discord write gate or tracker.db stayed busy.</summary>
    public const string Busy = "busy";
}
