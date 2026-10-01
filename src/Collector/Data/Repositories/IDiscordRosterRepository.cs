using Collector.Discord;

namespace Collector.Data.Repositories;

/// <summary>A computed sync ready to be written, with who sent it and when.</summary>
public sealed record DiscordSyncWrite(
    NormalizedSync Sync, RosterPlan Plan, DateTime CollectedAt, DateTime ReceivedAt,
    long SubmittedByApiUserId, string SubmittedByUsername);

/// <summary>The discord_syncs row written, and the organization the server is linked to (null when unmapped).</summary>
public sealed record DiscordSyncResult(long SyncId, string? OrgSid);

/// <summary>Reads and writes of a server's Discord roster for the ingestion (spec § 9).</summary>
public interface IDiscordRosterRepository
{
    /// <summary>Whether the server is in discord_guild_optouts (its syncs are refused).</summary>
    Task<bool> IsGuildExcludedAsync(string guildId, CancellationToken ct = default);

    /// <summary>
    /// Latest durable ReceivedAt, including hidden interrupted attempts whose effects must
    /// not be overwritten by an older collection. Null while the server has no row.
    /// </summary>
    Task<DateTime?> GetLastSyncReceivedAtAsync(string guildId, CancellationToken ct = default);

    /// <summary>
    /// The stored state the diff compares a sync against: the server row, all its roles
    /// (deleted ones included), all its member rows (with the Id of each departed member's
    /// latest "left" event) and the stored accounts among <paramref name="payloadUserIds"/>.
    /// </summary>
    Task<RosterSnapshot> LoadSnapshotAsync(string guildId, IReadOnlyCollection<string> payloadUserIds, CancellationToken ct = default);

    /// <summary>The ids among <paramref name="userIds"/> that are in discord_optouts.</summary>
    Task<IReadOnlySet<string>> GetOptedOutAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>
    /// Creates a hidden pending journal row, then writes account/member effects together
    /// with their events in transactions of at most MaxRowsPerTransaction rows. Finalizes
    /// roles, guild fields and the journal only after every effect commits. Recovery closes
    /// interrupted journals as partial and preserves their authorship. Freshness touches are
    /// monotone and batched. Retries once after 2 s on SQLITE_BUSY, then throws DiscordStoreBusyException.
    /// </summary>
    Task<DiscordSyncResult> ApplyAsync(DiscordSyncWrite write, CancellationToken ct = default);
}
