using Collector.Api.Auth;

namespace Collector.Api.Services.Discord;

/// <summary>
/// activity_logs entries of the Discord admin actions (spec § 13.2), written after the
/// tracker.db transactions have committed. The static admin key has no api_users row, so its
/// actions are logged without a user. A failed write is logged and never changes the response,
/// because the change it describes has already happened.
/// </summary>
public static class DiscordAudit
{
    public const string EraseAccount = "discord_erase_account";
    public const string EraseGuild = "discord_erase_guild";
    public const string RemoveOptOut = "discord_remove_optout";
    public const string RemoveGuildOptOut = "discord_remove_guild_optout";

    public static async Task LogAsync(ActivityLogService logs, CurrentUserAccessor user, ILogger logger,
        string action, string entityType, string entityId, CancellationToken ct)
    {
        try
        {
            var userId = user.UserId > 0 ? user.UserId : null;
            await logs.LogAsync(action, userId, entityType, entityId, user.IpAddress, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the {Action} audit entry for {EntityType} {EntityId}",
                action, entityType, entityId);
        }
    }
}
