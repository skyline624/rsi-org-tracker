using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Data.Repositories;

/// <summary>
/// Admin erasures and exclusions of Discord data. Every write goes through
/// <see cref="DiscordSqliteRetry"/> like the ingestion's, so a tracker.db held by the collector
/// ends in <see cref="Collector.Discord.DiscordStoreBusyException"/> (503), never in a 500.
/// Erasures use bounded transactions in an order that a second run completes.
/// </summary>
public sealed class DiscordErasureRepository : IDiscordErasureRepository
{
    /// <summary>Guild event and sync rows deleted per transaction.</summary>
    public const int RowsPerTransaction = DiscordRosterRepository.MaxRowsPerTransaction;

    /// <summary>
    /// Member rows deleted per transaction, together with at most one orphan account per member.
    /// Their event and rejection rows are removed in separate bounded transactions first.
    /// </summary>
    public const int MembersPerTransaction = 1000;

    private readonly TrackerDbContext _db;

    public DiscordErasureRepository(TrackerDbContext db)
    {
        _db = db;
    }

    /// <summary>How long a transaction waits for another connection's write lock before SQLite reports busy.</summary>
    public TimeSpan BusyTimeout { get; init; } = DiscordSqliteRetry.DefaultBusyTimeout;

    /// <summary>Waits before the one retry of a busy transaction; tests replace it so they neither sleep nor race.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Clock of the opt-outs' CreatedAt; tests pin it.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public async Task EraseAccountAsync(string discordUserId, long? byApiUserId, string byUsername, CancellationToken ct = default)
    {
        // Durable exclusion comes before any deletion: an interrupted erasure cannot let a
        // subsequent ingestion recreate rows. Repeating the erasure preserves the original actor.
        await WriteAsync(async () =>
        {
            if (await _db.DiscordOptOuts.AnyAsync(o => o.DiscordUserId == discordUserId, ct)) return;
            _db.DiscordOptOuts.Add(new DiscordOptOut
            {
                DiscordUserId = discordUserId, CreatedAt = Now, ByApiUserId = byApiUserId, ByUsername = byUsername,
            });
            await _db.SaveChangesAsync(ct);
        }, ct);

        await DeleteInBatchesAsync(() => _db.DiscordMembers.Where(m => m.DiscordUserId == discordUserId).OrderBy(m => m.Id).Take(RowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => _db.DiscordMemberEvents.Where(e => e.DiscordUserId == discordUserId).OrderBy(e => e.Id).Take(RowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => _db.DiscordLinkRejections.Where(r => r.DiscordUserId == discordUserId).OrderBy(r => r.Id).Take(RowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => _db.EntityLinks.Where(l => l.Provider == LinkProviders.Discord && l.Value == discordUserId).OrderBy(l => l.Id).Take(RowsPerTransaction), ct);
        await WriteAsync(() => _db.DiscordAccounts.Where(a => a.DiscordUserId == discordUserId).ExecuteDeleteAsync(ct), ct);
    }

    public async Task<bool> EraseGuildAsync(string guildId, bool exclude, long? byApiUserId, string byUsername, CancellationToken ct = default)
    {
        var known = await _db.DiscordGuilds.AnyAsync(g => g.GuildId == guildId, ct);
        if (!known && !exclude) return false;

        // The exclusion first: however far the erasure gets, later syncs are refused.
        if (exclude)
        {
            await WriteAsync(async () =>
            {
                if (await _db.DiscordGuildOptOuts.AnyAsync(o => o.GuildId == guildId, ct)) return;
                _db.DiscordGuildOptOuts.Add(new DiscordGuildOptOut
                {
                    GuildId = guildId, CreatedAt = Now, ByApiUserId = byApiUserId, ByUsername = byUsername,
                });
                await _db.SaveChangesAsync(ct);
            }, ct);
        }

        await DeleteInBatchesAsync(() => _db.DiscordMemberEvents.Where(e => e.GuildId == guildId).OrderBy(e => e.Id).Take(RowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => _db.DiscordSyncs.Where(s => s.GuildId == guildId).OrderBy(s => s.Id).Take(RowsPerTransaction), ct);
        await DeleteInBatchesAsync(() => _db.DiscordRoles.Where(r => r.GuildId == guildId).OrderBy(r => r.Id).Take(RowsPerTransaction), ct);

        // Leave the membership rows as durable retry pointers until each prospective orphan's
        // global history has been cleaned. A failure in any child batch still leaves the account
        // discoverable on the next run. Linked accounts and accounts in another guild are kept.
        while (true)
        {
            var batch = await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId)
                .OrderBy(m => m.Id)
                .Select(m => m.DiscordUserId)
                .Take(MembersPerTransaction)
                .ToArrayAsync(ct);
            if (batch.Length == 0) break;

            var orphans = await _db.DiscordAccounts.AsNoTracking()
                .Where(a => batch.Contains(a.DiscordUserId))
                .Where(a => !_db.DiscordMembers.Any(m => m.DiscordUserId == a.DiscordUserId && m.GuildId != guildId))
                .Where(a => !_db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == a.DiscordUserId))
                .Select(a => a.DiscordUserId)
                .ToArrayAsync(ct);

            if (orphans.Length > 0)
            {
                await DeleteInBatchesAsync(() => _db.DiscordMemberEvents.Where(e => e.GuildId == null && orphans.Contains(e.DiscordUserId)).OrderBy(e => e.Id).Take(RowsPerTransaction), ct);
                await DeleteInBatchesAsync(() => _db.DiscordLinkRejections.Where(r => orphans.Contains(r.DiscordUserId)).OrderBy(r => r.Id).Take(RowsPerTransaction), ct);
            }

            await WriteAsync(async () =>
            {
                await _db.DiscordMembers.Where(m => m.GuildId == guildId && batch.Contains(m.DiscordUserId)).ExecuteDeleteAsync(ct);
                if (orphans.Length > 0)
                    await _db.DiscordAccounts.Where(a => orphans.Contains(a.DiscordUserId)).ExecuteDeleteAsync(ct);
            }, ct);
        }

        // Accounts may have committed before the very first membership batch of an interrupted
        // ingestion. Such rows have no guild pointer; sweep all unlinked orphans as §13.2 requires.
        // Keep each account row until its child cleanup finishes, making retries discover it again.
        while (true)
        {
            var orphans = await _db.DiscordAccounts.AsNoTracking()
                .Where(a => !_db.DiscordMembers.Any(m => m.DiscordUserId == a.DiscordUserId))
                .Where(a => !_db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == a.DiscordUserId))
                .OrderBy(a => a.Id)
                .Select(a => a.DiscordUserId)
                .Take(MembersPerTransaction)
                .ToArrayAsync(ct);
            if (orphans.Length == 0) break;
            await DeleteInBatchesAsync(() => _db.DiscordMemberEvents.Where(e => e.GuildId == null && orphans.Contains(e.DiscordUserId)).OrderBy(e => e.Id).Take(RowsPerTransaction), ct);
            await DeleteInBatchesAsync(() => _db.DiscordLinkRejections.Where(r => orphans.Contains(r.DiscordUserId)).OrderBy(r => r.Id).Take(RowsPerTransaction), ct);
            await WriteAsync(() => _db.DiscordAccounts.Where(a => orphans.Contains(a.DiscordUserId)).ExecuteDeleteAsync(ct), ct);
        }

        // Last: while this row exists, the erasure can be run again to finish the job.
        await WriteAsync(() => _db.DiscordGuilds.Where(g => g.GuildId == guildId).ExecuteDeleteAsync(ct), ct);
        return true;
    }

    public async Task<IReadOnlyList<DiscordOptOut>> ListOptOutsAsync(CancellationToken ct = default) =>
        await _db.DiscordOptOuts.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .ToListAsync(ct);

    public async Task<bool> RemoveOptOutAsync(string discordUserId, CancellationToken ct = default)
    {
        var removed = 0;
        await WriteAsync(async () => removed = await _db.DiscordOptOuts
            .Where(o => o.DiscordUserId == discordUserId)
            .ExecuteDeleteAsync(ct), ct);
        return removed > 0;
    }

    public async Task<IReadOnlyList<DiscordGuildOptOut>> ListGuildOptOutsAsync(CancellationToken ct = default) =>
        await _db.DiscordGuildOptOuts.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .ToListAsync(ct);

    public async Task<bool> RemoveGuildOptOutAsync(string guildId, CancellationToken ct = default)
    {
        var removed = 0;
        await WriteAsync(async () => removed = await _db.DiscordGuildOptOuts
            .Where(o => o.GuildId == guildId)
            .ExecuteDeleteAsync(ct), ct);
        return removed > 0;
    }

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    private Task WriteAsync(Func<Task> work, CancellationToken ct) =>
        DiscordSqliteRetry.RunAsync(_db, work, BusyTimeout, Delay, ct);

    /// <summary>Deletes what <paramref name="batch"/> selects, one bounded transaction at a time, until a batch comes back short.</summary>
    private async Task DeleteInBatchesAsync<T>(Func<IQueryable<T>> batch, CancellationToken ct)
    {
        var deleted = 0;
        do
        {
            await WriteAsync(async () => deleted = await batch().ExecuteDeleteAsync(ct), ct);
        }
        while (deleted == RowsPerTransaction);
    }
}
