namespace Collector.Data.Repositories;

/// <summary>
/// Deletes the Discord data the retention rules (spec § 13.3) no longer allow to keep. Each
/// call handles one account/receipt batch under the caller's Discord write gate. Large child
/// histories are removed in transactions of at most 5,000 rows, with accounts deleted last
/// so an interrupted pass can resume. Linked and recently observed accounts are preserved.
/// </summary>
public interface IDiscordRetentionRepository
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> discord_syncs rows received before
    /// <paramref name="before"/>, oldest first. Returns how many were deleted.
    /// </summary>
    Task<int> PurgeSyncLogsAsync(DateTime before, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Purges up to <paramref name="batchSize"/> accounts no entity_links row points to and
    /// whose LastSeenAt is older than the cutoff and which either have member rows that all satisfy coalesce(LeftAt, LastSeenAt) &lt;
    /// <paramref name="before"/>, or have no member row and a LastSeenAt before it; with
    /// their discord_members, discord_member_events and discord_link_rejections rows.
    /// Returns how many accounts were purged.
    /// </summary>
    Task<int> PurgeDepartedAccountsAsync(DateTime before, int batchSize, CancellationToken ct = default);
}
