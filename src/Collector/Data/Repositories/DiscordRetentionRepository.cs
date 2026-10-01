using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Data.Repositories;

/// <summary>
/// Retention purges on the Discord tables, one account batch per call and bounded row batches per transaction
/// (see <see cref="IDiscordRetentionRepository"/>).
/// </summary>
public sealed class DiscordRetentionRepository(TrackerDbContext db) : IDiscordRetentionRepository
{
    public const int MaxRowsPerTransaction = DiscordRosterRepository.MaxRowsPerTransaction;

    public async Task<int> PurgeSyncLogsAsync(DateTime before, int batchSize, CancellationToken ct = default)
    {
        CheckBatchSize(batchSize);
        var deleted = 0;
        await WriteAsync(async () =>
        {
            var ids = await db.DiscordSyncs.AsNoTracking()
                .Where(s => s.ReceivedAt < before && s.EventCount >= 0)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            deleted = ids.Count == 0 ? 0 : await db.DiscordSyncs.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }, ct);
        return deleted;
    }

    public async Task<int> PurgeDepartedAccountsAsync(DateTime before, int batchSize, CancellationToken ct = default)
    {
        CheckBatchSize(batchSize);

        // Linked accounts are left out of the candidates, so they never fill a batch and stop
        // the caller's loop early.
        var candidates = await db.DiscordAccounts.AsNoTracking()
            .Where(a => a.LastSeenAt < before)
            .Where(a => !db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == a.DiscordUserId))
            .Where(a => !db.DiscordMembers.Any(m => m.DiscordUserId == a.DiscordUserId
                && ((m.LeftAt != null && m.LeftAt >= before) || (m.LeftAt == null && m.LastSeenAt >= before))))
            .OrderBy(a => a.Id)
            .Select(a => a.DiscordUserId)
            .Take(batchSize)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return 0;

        // entity_links is read again right before the deletes (spec § 13.3): a link made since
        // the candidates were chosen keeps its account.
        var linked = await db.EntityLinks.AsNoTracking()
            .Where(l => l.Provider == LinkProviders.Discord && candidates.Contains(l.Value))
            .Select(l => l.Value)
            .ToListAsync(ct);
        var ids = candidates.Except(linked, StringComparer.Ordinal).ToList();

        // Accounts remain durable retry pointers until all their children are gone. A single
        // old account may have many years of events; bound rows rather than only account count.
        await DeleteInBatchesAsync(() => db.DiscordMemberEvents.Where(e => ids.Contains(e.DiscordUserId))
            .OrderBy(e => e.Id).Take(MaxRowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => db.DiscordLinkRejections.Where(r => ids.Contains(r.DiscordUserId))
            .OrderBy(r => r.Id).Take(MaxRowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => db.DiscordMembers.Where(m => ids.Contains(m.DiscordUserId))
            .OrderBy(m => m.Id).Take(MaxRowsPerTransaction), ct);
        var purged = 0;
        await WriteAsync(async () => purged = await db.DiscordAccounts
            .Where(a => ids.Contains(a.DiscordUserId) && a.LastSeenAt < before
                && !db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == a.DiscordUserId))
            .ExecuteDeleteAsync(ct), ct);
        return purged;
    }

    private Task WriteAsync(Func<Task> work, CancellationToken ct) => DiscordSqliteRetry.RunAsync(
        db, work, DiscordSqliteRetry.DefaultBusyTimeout, Task.Delay, ct);

    private async Task DeleteInBatchesAsync<T>(Func<IQueryable<T>> rows, CancellationToken ct)
    {
        int deleted;
        do
        {
            deleted = 0;
            await WriteAsync(async () => deleted = await rows().ExecuteDeleteAsync(ct), ct);
        } while (deleted == MaxRowsPerTransaction);
    }

    private static void CheckBatchSize(int batchSize)
    {
        if (batchSize is < 1 or > MaxRowsPerTransaction)
            throw new ArgumentOutOfRangeException(nameof(batchSize), $"A retention batch must contain 1–{MaxRowsPerTransaction} accounts or receipts.");
    }
}
