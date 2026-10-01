using Collector.Models;

namespace Collector.Data.Repositories;

/// <summary>
/// GDPR erasure and opposition for the Discord rosters (spec § 13.2), plus an admin's one-off
/// lift of the mass-departure guard (§ 9.3). Callers hold the Discord write gate.
/// </summary>
public interface IDiscordErasureRepository
{
    /// <summary>
    /// Deletes the account's discord_members, discord_member_events, discord_accounts and
    /// discord_link_rejections rows and its entity_links of provider "discord" in bounded batches.
    /// Records the opt-out first (kept as is when one exists), so interrupted erasures still
    /// prevent later syncs from recreating the account and can safely be repeated.
    /// </summary>
    Task EraseAccountAsync(string discordUserId, long? byApiUserId, string byUsername, CancellationToken ct = default);

    /// <summary>
    /// Deletes the guild's roles, members, events, syncs and guild row, and the unlinked accounts
    /// left with no member row anywhere, with their account events and rejections. With
    /// <paramref name="exclude"/>, also records a guild opt-out, even for a guild never synced.
    /// False when the guild is unknown and <paramref name="exclude"/> is false.
    /// </summary>
    Task<bool> EraseGuildAsync(string guildId, bool exclude, long? byApiUserId, string byUsername, CancellationToken ct = default);

    /// <summary>Every account opt-out, newest first.</summary>
    Task<IReadOnlyList<DiscordOptOut>> ListOptOutsAsync(CancellationToken ct = default);

    /// <summary>Lets later syncs record the account again. False when it had no opt-out.</summary>
    Task<bool> RemoveOptOutAsync(string discordUserId, CancellationToken ct = default);

    /// <summary>Every guild opt-out, newest first.</summary>
    Task<IReadOnlyList<DiscordGuildOptOut>> ListGuildOptOutsAsync(CancellationToken ct = default);

    /// <summary>Accepts the guild's syncs again (the next one is a baseline). False when it had no opt-out.</summary>
    Task<bool> RemoveGuildOptOutAsync(string guildId, CancellationToken ct = default);

}
