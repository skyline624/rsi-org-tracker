using System.Text.Json;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Data.Repositories;

/// <summary>
/// Reads the stored roster of a server and writes a sync's <see cref="RosterPlan"/> in bounded
/// transactions (spec § 9.4). tracker.db is shared with the collector, whose writes wait at
/// most busy_timeout (5 s) and never retry. Every transaction has a budget of 5,000 rows,
/// keeping each account/member effect together with its events. An interrupted journal entry
/// stays hidden until recovery closes it as partial; a later collection resumes without losing
/// arrivals or duplicating events. Completeness is published only after all effects commit.
/// </summary>
public sealed class DiscordRosterRepository : IDiscordRosterRepository
{
    /// <summary>Largest number of rows any transaction writes, including the events coupled to each state change.</summary>
    public const int MaxRowsPerTransaction = 5000;

    private readonly TrackerDbContext _db;

    public DiscordRosterRepository(TrackerDbContext db)
    {
        _db = db;
    }

    /// <summary>How long a transaction waits for another connection's write lock before SQLite reports busy.</summary>
    public TimeSpan BusyTimeout { get; init; } = DiscordSqliteRetry.DefaultBusyTimeout;

    /// <summary>Waits before the one retry of a busy transaction; tests replace it so they neither sleep nor race.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public Task<bool> IsGuildExcludedAsync(string guildId, CancellationToken ct = default) =>
        _db.DiscordGuildOptOuts.AnyAsync(o => o.GuildId == guildId, ct);

    public async Task<DateTime?> GetLastSyncReceivedAtAsync(string guildId, CancellationToken ct = default)
    {
        var receivedAt = await _db.DiscordGuilds.AsNoTracking()
            .Where(g => g.GuildId == guildId)
            .Select(g => (DateTime?)g.LastSyncAt)
            .SingleOrDefaultAsync(ct);
        // A failed attempt can already contain durable effects. Older concurrent collections
        // must not overwrite them, even before the pending journal row is closed as partial.
        var attemptAt = await _db.DiscordSyncs.Where(s => s.GuildId == guildId)
            .MaxAsync(s => (DateTime?)s.ReceivedAt, ct);
        return attemptAt > receivedAt || receivedAt is null ? attemptAt : receivedAt;
    }

    public async Task<RosterSnapshot> LoadSnapshotAsync(string guildId, IReadOnlyCollection<string> payloadUserIds, CancellationToken ct = default)
    {
        var guild = await _db.DiscordGuilds.AsNoTracking()
            .Where(g => g.GuildId == guildId)
            .Select(g => new GuildSnapshot(g.FirstSyncAt, g.LastCompleteSyncAt,
                _db.DiscordSyncs.Any(s => s.GuildId == guildId && s.IsBaseline && s.EventCount < 0)))
            .SingleOrDefaultAsync(ct);

        var roles = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .Select(r => new { r.RoleId, r.Name, r.DeletedAt })
            .ToListAsync(ct);

        var members = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => new { m.DiscordUserId, m.Nick, m.RoleIdsJson, m.JoinedAt, m.LastSeenAt, m.LeftAt })
            .ToListAsync(ct);

        // A false departure deletes the member's latest "left" event of this server.
        var lastLeftEventIds = await _db.DiscordMemberEvents.AsNoTracking()
            .Where(e => e.GuildId == guildId && e.Type == DiscordEventTypes.Left)
            .GroupBy(e => e.DiscordUserId)
            .Select(g => new { UserId = g.Key, Id = g.Max(e => e.Id) })
            .ToDictionaryAsync(e => e.UserId, e => e.Id, StringComparer.Ordinal, ct);

        var ids = payloadUserIds.ToArray();
        var accounts = await _db.DiscordAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.DiscordUserId))
            .Select(a => new AccountSnapshot(a.DiscordUserId, a.Username, a.GlobalName, a.IsBot, a.LastSeenAt))
            .ToDictionaryAsync(a => a.UserId, StringComparer.Ordinal, ct);

        return new RosterSnapshot(
            guild,
            roles.ToDictionary(r => r.RoleId, r => new RoleSnapshot(r.RoleId, r.Name, r.DeletedAt != null), StringComparer.Ordinal),
            members.ToDictionary(
                m => m.DiscordUserId,
                m => new MemberSnapshot(m.DiscordUserId, m.Nick, ParseRoleIds(m.RoleIdsJson), m.JoinedAt, m.LastSeenAt, m.LeftAt,
                    m.LeftAt != null && lastLeftEventIds.TryGetValue(m.DiscordUserId, out var eventId) ? eventId : null),
                StringComparer.Ordinal),
            accounts);
    }

    public async Task<IReadOnlySet<string>> GetOptedOutAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        var ids = userIds.ToArray();
        var optedOut = await _db.DiscordOptOuts.AsNoTracking()
            .Where(o => ids.Contains(o.DiscordUserId))
            .Select(o => o.DiscordUserId)
            .ToListAsync(ct);
        return optedOut.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<DiscordSyncResult> ApplyAsync(DiscordSyncWrite write, CancellationToken ct = default)
    {
        var sync = write.Sync;
        var plan = write.Plan;
        var collectedAt = write.CollectedAt;
        long syncId = 0;

        // Persist authorship before writing any effects. Negative EventCount hides unfinished
        // attempts from reads, including legacy -2 markers.
        await WriteAsync(async () =>
        {
            var guild = await _db.DiscordGuilds.SingleOrDefaultAsync(g => g.GuildId == sync.GuildId, ct);
            if (guild is null)
            {
                guild = new DiscordGuild
                {
                    GuildId = sync.GuildId, Name = sync.GuildName, IconHash = sync.IconHash,
                    MemberCount = sync.MemberCount, FirstSyncAt = collectedAt,
                    LastSyncAt = write.ReceivedAt, LastCollectedAt = collectedAt,
                    CreatedAt = write.ReceivedAt, UpdatedAt = write.ReceivedAt,
                };
                _db.DiscordGuilds.Add(guild);
            }
            var interrupted = await _db.DiscordSyncs.Where(s => s.GuildId == sync.GuildId && s.EventCount < 0).ToListAsync(ct);
            foreach (var pending in interrupted)
            {
                pending.IsComplete = false;
                pending.EventCount = await _db.DiscordMemberEvents.CountAsync(e => e.SyncId == pending.Id, ct);
                if (guild.LastSyncAt < pending.ReceivedAt)
                {
                    guild.LastSyncAt = pending.ReceivedAt;
                    guild.LastCollectedAt = pending.CollectedAt;
                    guild.UpdatedAt = pending.ReceivedAt;
                }
            }
            var row = SyncRow(write);
            row.EventCount = -1;
            row.IsComplete = false;
            _db.DiscordSyncs.Add(row);
            await _db.SaveChangesAsync(ct);
            syncId = row.Id;
        }, ct);

        var accountEvents = plan.Events.Where(e => e.GuildId is null).ToLookup(e => e.UserId, StringComparer.Ordinal);
        var accountUnits = plan.AccountsToInsert.Select(a => new AccountMutation(a, true, accountEvents[a.UserId].ToList()))
            .Concat(plan.AccountsToUpdate.Select(a => new AccountMutation(a, false, accountEvents[a.UserId].ToList())));
        foreach (var batch in Batches(accountUnits, u => 1 + u.Events.Count))
        {
            await WriteAsync(async () =>
            {
                var ids = batch.Where(u => !u.Insert).Select(u => u.Write.UserId).ToArray();
                var rows = await _db.DiscordAccounts.Where(a => ids.Contains(a.DiscordUserId))
                    .ToDictionaryAsync(a => a.DiscordUserId, StringComparer.Ordinal, ct);
                foreach (var unit in batch)
                {
                    var account = unit.Write;
                    DiscordAccount row;
                    if (unit.Insert)
                    {
                        row = new DiscordAccount { DiscordUserId = account.UserId, FirstSeenAt = collectedAt };
                        _db.DiscordAccounts.Add(row);
                    }
                    else row = rows[account.UserId];
                    row.Username = account.Username;
                    row.GlobalName = account.GlobalName;
                    row.IsBot = account.IsBot;
                    if (row.LastSeenAt < collectedAt) row.LastSeenAt = collectedAt;
                    AddEvents(syncId, unit.Events);
                }
                await _db.SaveChangesAsync(ct);
            }, ct);
        }

        var guildEvents = plan.Events.Where(e => e.GuildId is not null).ToLookup(e => e.UserId, StringComparer.Ordinal);
        var correctionIds = plan.EventIdsToDelete.ToArray();
        var corrections = await _db.DiscordMemberEvents.AsNoTracking()
            .Where(e => e.GuildId == sync.GuildId && correctionIds.Contains(e.Id))
            .Select(e => new { e.DiscordUserId, e.Id }).ToListAsync(ct);
        var correctionsByUser = corrections.ToLookup(e => e.DiscordUserId, e => e.Id, StringComparer.Ordinal);
        var memberUnits = plan.MembersToInsert.Select(m => new MemberMutation(m, true,
                guildEvents[m.UserId].ToList(), correctionsByUser[m.UserId].ToList()))
            .Concat(plan.MembersToUpdate.Select(m => new MemberMutation(m, false,
                guildEvents[m.UserId].ToList(), correctionsByUser[m.UserId].ToList())));
        foreach (var batch in Batches(memberUnits, u => 1 + u.Events.Count + u.EventIdsToDelete.Count))
        {
            await WriteAsync(async () =>
            {
                var ids = batch.Where(u => !u.Insert).Select(u => u.Write.UserId).ToArray();
                var rows = await _db.DiscordMembers.Where(m => m.GuildId == sync.GuildId && ids.Contains(m.DiscordUserId))
                    .ToDictionaryAsync(m => m.DiscordUserId, StringComparer.Ordinal, ct);
                foreach (var unit in batch)
                {
                    var member = unit.Write;
                    DiscordMember row;
                    if (unit.Insert)
                    {
                        row = new DiscordMember
                        {
                            GuildId = sync.GuildId, DiscordUserId = member.UserId,
                            FirstSeenAt = collectedAt, LastSeenAt = collectedAt,
                        };
                        _db.DiscordMembers.Add(row);
                    }
                    else row = rows[member.UserId];
                    row.Nick = member.Nick;
                    row.RoleIdsJson = SerializeRoleIds(member.RoleIds);
                    row.JoinedAt = member.JoinedAt;
                    row.LeftAt = member.LeftAt;
                    if (member.LeftAt is null && row.LastSeenAt < collectedAt) row.LastSeenAt = collectedAt;
                    if (unit.EventIdsToDelete.Count > 0)
                    {
                        var deleted = unit.EventIdsToDelete.ToArray();
                        await _db.DiscordMemberEvents.Where(e => e.GuildId == sync.GuildId && deleted.Contains(e.Id)).ExecuteDeleteAsync(ct);
                    }
                    AddEvents(syncId, unit.Events);
                }
                await _db.SaveChangesAsync(ct);
            }, ct);
        }

        // Freshness is part of the write, so complete must remain unpublished if a touch batch fails.
        foreach (var batch in plan.PresentUserIds.Chunk(MaxRowsPerTransaction))
        {
            await WriteAsync(() => _db.DiscordMembers
                .Where(m => m.GuildId == sync.GuildId && batch.Contains(m.DiscordUserId) && m.LastSeenAt < collectedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSeenAt, collectedAt), ct), ct);
            await WriteAsync(() => _db.DiscordAccounts
                .Where(a => batch.Contains(a.DiscordUserId) && a.LastSeenAt < collectedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, collectedAt), ct), ct);
        }

        string? orgSid = null;
        // At most 250 old active and 250 new roles, one guild and one sync: bounded.
        // Completeness commits only after all member effects.
        await WriteAsync(async () =>
        {
            var guild = await _db.DiscordGuilds.SingleAsync(g => g.GuildId == sync.GuildId, ct);
            guild.Name = sync.GuildName;
            guild.IconHash = sync.IconHash;
            guild.MemberCount = sync.MemberCount;
            guild.LastSyncAt = write.ReceivedAt;
            guild.LastCollectedAt = collectedAt;
            guild.UpdatedAt = write.ReceivedAt;
            if (plan.IsComplete) guild.LastCompleteSyncAt = collectedAt;
            await WriteRolesAsync(sync.GuildId, plan, collectedAt, ct);
            var row = await _db.DiscordSyncs.SingleAsync(s => s.Id == syncId, ct);
            row.IsComplete = plan.IsComplete;
            row.EventCount = await _db.DiscordMemberEvents.CountAsync(e => e.SyncId == syncId, ct);
            await _db.SaveChangesAsync(ct);
            orgSid = guild.OrgSid;
        }, ct);

        return new DiscordSyncResult(syncId, orgSid);
    }

    private sealed record AccountMutation(AccountWrite Write, bool Insert, IReadOnlyList<PlannedEvent> Events);
    private sealed record MemberMutation(MemberWrite Write, bool Insert, IReadOnlyList<PlannedEvent> Events, IReadOnlyList<long> EventIdsToDelete);

    /// <summary>Groups effects by their actual row budget without splitting an event from the state it records.</summary>
    private static IEnumerable<IReadOnlyList<T>> Batches<T>(IEnumerable<T> units, Func<T, int> cost)
    {
        var batch = new List<T>();
        var rows = 0;
        foreach (var unit in units)
        {
            var required = cost(unit);
            if (rows + required > MaxRowsPerTransaction && batch.Count > 0)
            {
                yield return batch;
                batch = [];
                rows = 0;
            }
            if (required > MaxRowsPerTransaction) throw new InvalidOperationException("One Discord effect exceeds the transaction row budget.");
            batch.Add(unit);
            rows += required;
        }
        if (batch.Count > 0) yield return batch;
    }

    private void AddEvents(long syncId, IReadOnlyList<PlannedEvent> events) =>
        _db.DiscordMemberEvents.AddRange(events.Select(e => new DiscordMemberEvent
        {
            GuildId = e.GuildId, DiscordUserId = e.UserId, SyncId = syncId, Type = e.Type,
            OldValue = e.OldValue, NewValue = e.NewValue,
            OccurredAt = e.OccurredAt, NotBefore = e.NotBefore, ObservedAt = e.ObservedAt,
        }));

    private static DiscordSync SyncRow(DiscordSyncWrite write) => new()
    {
        GuildId = write.Sync.GuildId,
        SubmittedByApiUserId = write.SubmittedByApiUserId,
        SubmittedByUsername = write.SubmittedByUsername,
        ReceivedAt = write.ReceivedAt, CollectedAt = write.CollectedAt,
        DeclaredCollectedAt = write.Sync.DeclaredCollectedAt,
        Method = write.Sync.Method, DeclaredComplete = write.Sync.DeclaredComplete,
        IsBaseline = write.Plan.IsBaseline, MassDepartureDetected = write.Plan.MassDepartureDetected,
        ExpectedCount = write.Sync.ExpectedCount, CollectedCount = write.Sync.CollectedCount,
        OptedOutCount = write.Plan.OptedOutCount, UnknownRoleRefCount = write.Sync.UnknownRoleRefCount,
        PluginVersion = write.Sync.PluginVersion,
    };

    private Task WriteAsync(Func<Task> work, CancellationToken ct) =>
        DiscordSqliteRetry.RunAsync(_db, work, BusyTimeout, Delay, ct);
    /// <summary>Roles are always sent whole. IsRank and RankOrder are set on insert only, RsiRankLabel never.</summary>
    private async Task WriteRolesAsync(string guildId, RosterPlan plan, DateTime collectedAt, CancellationToken ct)
    {
        var stored = await _db.DiscordRoles
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct);

        foreach (var role in plan.RolesToInsert)
        {
            _db.DiscordRoles.Add(new DiscordRole
            {
                GuildId = guildId, RoleId = role.RoleId, Name = role.Name, Position = role.Position, Color = role.Color,
                Hoist = role.Hoist, Managed = role.Managed,
                IsRank = DiscordRosterDiff.DefaultIsRank(role), RankOrder = DiscordRosterDiff.DefaultRankOrder(role),
                FirstSeenAt = collectedAt, LastSeenAt = collectedAt,
            });
        }

        foreach (var role in plan.RolesToUpdate)
        {
            if (!stored.TryGetValue(role.RoleId, out var row)) continue;
            row.Name = role.Name;
            row.Position = role.Position;
            row.Color = role.Color;
            row.Hoist = role.Hoist;
            row.Managed = role.Managed;
            row.LastSeenAt = row.LastSeenAt > collectedAt ? row.LastSeenAt : collectedAt;
            row.DeletedAt = null;
        }

        foreach (var roleId in plan.RoleIdsToMarkDeleted)
        {
            if (stored.TryGetValue(roleId, out var row) && row.DeletedAt is null) row.DeletedAt = collectedAt;
        }
    }

    /// <summary>The stored form of a member's roles: a JSON array of role id strings, sorted by the diff.</summary>
    private static string SerializeRoleIds(IReadOnlyList<string> roleIds) => JsonSerializer.Serialize(roleIds);

    private static IReadOnlyList<string> ParseRoleIds(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
