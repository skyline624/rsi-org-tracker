using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// The Discord side of a citizen's page (spec § 10.4) and of an org's page (spec § 11): the
/// Discord accounts linked to a person with their guilds and a combined RSI + Discord
/// timeline, and the guilds mapped to an org. Everything is read on request, so a guild
/// re-mapped to another org moves between the org pages at once.
/// </summary>
public sealed class DiscordProfileService(TrackerDbContext db)
{
    public const int MaxTimelineEntries = 100;

    /// <summary>The RSI changes the combined timeline keeps (spec § 10.4).</summary>
    private static readonly string[] RsiTimelineTypes =
        ["member_joined", "member_left", "rank_changed", "roles_changed", "handle_changed"];

    /// <summary>
    /// Linked accounts, their guilds and the combined timeline of the person known as
    /// <paramref name="handle"/> (regardless of case). Empty lists when the person has no
    /// Discord data; 404 when no source knows the handle.
    /// </summary>
    public async Task<DiscordUserProfileDto> GetUserProfileAsync(string handle, CancellationToken ct)
    {
        var wanted = handle.Trim();
        var (known, entity) = await ResolvePersonAsync(wanted, ct);
        if (!known) throw new NotFoundException($"Citoyen inconnu : « {wanted} ».");
        if (entity is null) return new DiscordUserProfileDto();

        var linkedIds = await db.EntityLinks.AsNoTracking()
            .Where(l => l.TrackedEntityId == entity.Id && l.Provider == LinkProviders.Discord)
            .Select(l => l.Value)
            .Distinct()
            .ToListAsync(ct);
        if (linkedIds.Count == 0) return new DiscordUserProfileDto();
        var accounts = await db.DiscordAccounts.AsNoTracking()
            .Where(a => linkedIds.Contains(a.DiscordUserId))
            .OrderBy(a => a.Username).ThenBy(a => a.DiscordUserId)
            .ToListAsync(ct);
        if (accounts.Count == 0) return new DiscordUserProfileDto();

        var accountIds = accounts.Select(a => a.DiscordUserId).ToList();
        var memberships = await db.DiscordMembers.AsNoTracking()
            .Where(m => accountIds.Contains(m.DiscordUserId))
            .ToListAsync(ct);
        var discordEvents = await db.DiscordMemberEvents.AsNoTracking()
            .Where(e => accountIds.Contains(e.DiscordUserId))
            .Where(e => !db.DiscordSyncs.Any(s => s.Id == e.SyncId && s.EventCount < 0))
            .OrderByDescending(e => e.OccurredAt ?? e.ObservedAt).ThenByDescending(e => e.Id)
            .Take(MaxTimelineEntries)
            .ToListAsync(ct);
        var guildIds = memberships.Select(m => m.GuildId)
            .Concat(discordEvents.Where(e => e.GuildId != null).Select(e => e.GuildId!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var guilds = (await db.DiscordGuilds.AsNoTracking()
                .Where(g => guildIds.Contains(g.GuildId))
                .Select(g => new { g.GuildId, g.Name, g.OrgSid })
                .ToListAsync(ct))
            .ToDictionary(g => g.GuildId, g => new GuildInfo(g.Name, g.OrgSid), StringComparer.Ordinal);
        var rolesByGuild = (await db.DiscordRoles.AsNoTracking().Where(r => guildIds.Contains(r.GuildId)).ToListAsync(ct))
            .GroupBy(r => r.GuildId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.RoleId, StringComparer.Ordinal), StringComparer.Ordinal);

        var entries = await RsiTimelineAsync(entity, ct);
        foreach (var e in discordEvents)
        {
            var guild = e.GuildId is not null ? guilds.GetValueOrDefault(e.GuildId) : null;
            entries.Add((new DiscordTimelineEntryDto
            {
                Source = DiscordTimelineSources.Discord,
                Type = e.Type,
                At = e.OccurredAt ?? e.ObservedAt,
                NotBefore = e.OccurredAt is null ? e.NotBefore : null,
                OrgSid = guild?.OrgSid,
                GuildId = e.GuildId,
                GuildName = guild?.Name,
                OldValue = e.OldValue,
                NewValue = e.NewValue,
            }, e.Id));
        }

        return new DiscordUserProfileDto
        {
            Accounts = accounts.Select(a => new DiscordProfileAccountDto
            {
                DiscordUserId = a.DiscordUserId,
                Username = a.Username,
                GlobalName = a.GlobalName,
                Guilds = memberships
                    .Where(m => m.DiscordUserId == a.DiscordUserId)
                    .Select(m => new DiscordProfileGuildDto
                    {
                        GuildId = m.GuildId,
                        GuildName = guilds.GetValueOrDefault(m.GuildId)?.Name ?? m.GuildId,
                        OrgSid = guilds.GetValueOrDefault(m.GuildId)?.OrgSid,
                        Rank = rolesByGuild.TryGetValue(m.GuildId, out var roles)
                            ? DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(m.RoleIdsJson), roles)?.Name
                            : null,
                        JoinedAt = m.JoinedAt,
                        LeftAt = m.LeftAt,
                        LastSeenAt = m.LastSeenAt,
                    })
                    .OrderBy(g => g.LeftAt is null ? 0 : 1)
                    .ThenBy(g => g.GuildName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.GuildId, StringComparer.Ordinal)
                    .ToList(),
            }).ToList(),
            Timeline = entries
                .OrderByDescending(x => x.Entry.At)
                .ThenBy(x => x.Entry.Source, StringComparer.Ordinal)
                .ThenByDescending(x => x.Id)
                .Take(MaxTimelineEntries)
                .Select(x => x.Entry)
                .ToList(),
        };
    }

    /// <summary>The guilds mapped to the org (SID regardless of case), by name; empty when none.</summary>
    public async Task<IReadOnlyList<DiscordOrgGuildDto>> GetOrgGuildsAsync(string sid, CancellationToken ct)
    {
        var orgSid = sid.Trim().ToUpperInvariant();
        var guilds = await db.DiscordGuilds.AsNoTracking().Where(g => g.OrgSid == orgSid).ToListAsync(ct);
        if (guilds.Count == 0) return [];

        var guildIds = guilds.Select(g => g.GuildId).ToList();
        var humans = from m in db.DiscordMembers.AsNoTracking()
                     join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                     where guildIds.Contains(m.GuildId) && m.LeftAt == null && !a.IsBot
                     select m;
        var active = await humans
            .GroupBy(m => m.GuildId)
            .Select(g => new { GuildId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GuildId, x => x.Count, StringComparer.Ordinal, ct);
        var linked = await humans
            .Where(m => db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == m.DiscordUserId))
            .GroupBy(m => m.GuildId)
            .Select(g => new { GuildId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GuildId, x => x.Count, StringComparer.Ordinal, ct);

        return guilds
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GuildId, StringComparer.Ordinal)
            .Select(g => new DiscordOrgGuildDto
            {
                GuildId = g.GuildId,
                Name = g.Name,
                IconHash = g.IconHash,
                ActiveMembers = active.GetValueOrDefault(g.GuildId),
                LinkedMembers = linked.GetValueOrDefault(g.GuildId),
                LastSyncAt = g.LastSyncAt,
                // LastCompleteSyncAt is the CollectedAt of the last complete sync.
                LastSyncComplete = g.LastCompleteSyncAt is not null && g.LastCompleteSyncAt == g.LastCollectedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Resolves a handle as the links and memberships routes do, regardless of case: a citizen
    /// id from users, else from user_handle_history; the entity of that citizen id, else the
    /// entity whose current handle it is (never one bound to another citizen id). Known also
    /// when only an org roster has the handle.
    /// </summary>
    private async Task<(bool Known, TrackedEntity? Entity)> ResolvePersonAsync(string handle, CancellationToken ct)
    {
        var citizenId = await db.Users.AsNoTracking()
            .Where(u => EF.Functions.Collate(u.UserHandle, "NOCASE") == handle)
            .OrderByDescending(u => u.UpdatedAt)
            .Select(u => (int?)u.CitizenId)
            .FirstOrDefaultAsync(ct);
        citizenId ??= await db.UserHandleHistories.AsNoTracking()
            .Where(h => EF.Functions.Collate(h.UserHandle, "NOCASE") == handle)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => (int?)h.CitizenId)
            .FirstOrDefaultAsync(ct);

        TrackedEntity? entity = null;
        if (citizenId is int cid)
            entity = await db.TrackedEntities.AsNoTracking().FirstOrDefaultAsync(e => e.CitizenId == cid, ct);
        entity ??= await db.TrackedEntities.AsNoTracking()
            .Where(e => e.CurrentHandle != null
                && EF.Functions.Collate(e.CurrentHandle, "NOCASE") == handle
                && (citizenId == null || e.CitizenId == null || e.CitizenId == citizenId))
            .OrderBy(e => e.Id)
            .FirstOrDefaultAsync(ct);

        if (entity is not null || citizenId is not null) return (true, entity);
        var inRoster = await db.OrganizationMembers.AsNoTracking()
            .AnyAsync(m => EF.Functions.Collate(m.UserHandle, "NOCASE") == handle, ct);
        return (inRoster, null);
    }

    /// <summary>
    /// RSI member events within the person's handle windows. A user-level rename has a
    /// permanent citizen id in EntityId, so it is read by that identity instead of a handle.
    /// </summary>
    private async Task<List<(DiscordTimelineEntryDto Entry, long Id)>> RsiTimelineAsync(TrackedEntity entity, CancellationToken ct)
    {
        var entries = new List<(DiscordTimelineEntryDto Entry, long Id)>();
        foreach (var window in await HandleWindowsAsync(entity, ct))
        {
            var query = db.ChangeEvents.AsNoTracking()
                .Where(c => c.UserHandle == window.Handle && c.EntityType == "member"
                    && RsiTimelineTypes.Contains(c.ChangeType));
            if (window.From is DateTime from) query = query.Where(c => c.Timestamp >= from);
            if (window.Through is DateTime through) query = query.Where(c => c.Timestamp <= through);
            if (window.Before is DateTime before) query = query.Where(c => c.Timestamp < before);
            var rows = await query
                .OrderByDescending(c => c.Timestamp).ThenByDescending(c => c.Id)
                .Take(MaxTimelineEntries)
                .Select(c => new { c.Id, c.ChangeType, c.Timestamp, c.OrgSid, c.OldValue, c.NewValue })
                .ToListAsync(ct);
            entries.AddRange(rows.Select(c => (new DiscordTimelineEntryDto
            {
                Source = DiscordTimelineSources.Rsi,
                Type = c.ChangeType,
                At = c.Timestamp,
                NotBefore = null,
                OrgSid = c.OrgSid,
                OldValue = c.OldValue,
                NewValue = c.NewValue,
            }, c.Id)));
        }
        if (entity.CitizenId is int citizenId)
        {
            var identity = citizenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var renames = await db.ChangeEvents.AsNoTracking()
                .Where(c => c.EntityType == "user" && c.EntityId == identity && c.ChangeType == "handle_changed")
                .OrderByDescending(c => c.Timestamp).ThenByDescending(c => c.Id)
                .Take(MaxTimelineEntries)
                .ToListAsync(ct);
            entries.AddRange(renames.Select(c => (new DiscordTimelineEntryDto
            {
                Source = DiscordTimelineSources.Rsi, Type = c.ChangeType, At = c.Timestamp,
                OrgSid = c.OrgSid, OldValue = c.OldValue, NewValue = c.NewValue,
            }, c.Id)));
        }
        return entries.DistinctBy(e => e.Id).ToList();
    }

    /// <summary>
    /// Ordinary handles retain the historical upper bound at the next handle's first
    /// observation. A repeated or reassigned handle is restricted to separate observed
    /// intervals: FirstSeen/LastSeen are observations, never exact transfer dates. Overlapping
    /// observations of another citizen make an interval ambiguous; only uncontested endpoint
    /// observations remain. A manual entity without citizen id cannot claim a contested handle.
    /// </summary>
    private async Task<List<HandleWindow>> HandleWindowsAsync(TrackedEntity entity, CancellationToken ct)
    {
        var history = new List<HandleObservation>();
        if (entity.CitizenId is int citizenId)
        {
            history = await db.UserHandleHistories.AsNoTracking()
                .Where(h => h.CitizenId == citizenId)
                .OrderBy(h => h.FirstSeen).ThenBy(h => h.Id)
                .Select(h => new HandleObservation(h.UserHandle, h.FirstSeen, h.LastSeen))
                .ToListAsync(ct);
            var current = await db.Users.AsNoTracking()
                .Where(u => u.CitizenId == citizenId)
                .Select(u => new HandleObservation(u.UserHandle, u.UpdatedAt, u.UpdatedAt))
                .FirstOrDefaultAsync(ct);
            if (current is not null)
            {
                if (history.Count > 0 && history[^1].Handle == current.Handle)
                    history[^1] = history[^1] with { LastSeen = Max(history[^1].LastSeen, current.LastSeen) };
                else
                    history.Add(current);
            }
        }
        if (history.Count == 0 && entity.CurrentHandle is not null)
            history.Add(new HandleObservation(entity.CurrentHandle, entity.UpdatedAt, entity.UpdatedAt));
        if (history.Count == 0) return [];

        var handles = history.Select(h => h.Handle).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var others = await db.UserHandleHistories.AsNoTracking()
            .Where(h => (entity.CitizenId == null || h.CitizenId != entity.CitizenId)
                && handles.Contains(EF.Functions.Collate(h.UserHandle, "NOCASE")))
            .Select(h => new HandleObservation(h.UserHandle, h.FirstSeen, h.LastSeen))
            .ToListAsync(ct);
        others.AddRange(await db.Users.AsNoTracking()
            .Where(u => (entity.CitizenId == null || u.CitizenId != entity.CitizenId)
                && handles.Contains(EF.Functions.Collate(u.UserHandle, "NOCASE")))
            .Select(u => new HandleObservation(u.UserHandle, u.UpdatedAt, u.UpdatedAt))
            .ToListAsync(ct));
        var foreign = others.ToLookup(h => h.Handle, StringComparer.OrdinalIgnoreCase);

        var windows = new List<HandleWindow>();
        for (var i = 0; i < history.Count; i++)
        {
            var observation = history[i];
            var before = i + 1 < history.Count ? history[i + 1].FirstSeen : (DateTime?)null;
            var passes = history.Select((h, index) => (h, index))
                .Where(x => string.Equals(x.h.Handle, observation.Handle, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.index).ToList();
            var returned = history.Skip(passes[0]).Take(passes[^1] - passes[0] + 1)
                .Any(h => !string.Equals(h.Handle, observation.Handle, StringComparison.OrdinalIgnoreCase));
            var conflicting = foreign[observation.Handle].ToList();
            if (!returned && conflicting.Count == 0)
            {
                windows.Add(new HandleWindow(observation.Handle, null, null, before));
                continue;
            }
            if (entity.CitizenId is null) continue;

            var through = Max(observation.FirstSeen, observation.LastSeen);
            // A row whose last observation spans a later handle may be legacy aggregation of
            // several passes. It proves no continuous interval: keep its initial observation.
            if (before is DateTime next && through >= next) through = observation.FirstSeen;
            if (conflicting.Any(h => h.FirstSeen <= through && h.LastSeen >= observation.FirstSeen))
            {
                foreach (var endpoint in new[] { observation.FirstSeen, through }.Distinct())
                {
                    if (!conflicting.Any(h => h.FirstSeen <= endpoint && h.LastSeen >= endpoint))
                        windows.Add(new HandleWindow(observation.Handle, endpoint, endpoint, before));
                }
            }
            else
            {
                windows.Add(new HandleWindow(observation.Handle, observation.FirstSeen, through, before));
            }
        }
        return windows.Distinct().ToList();
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
    private sealed record HandleObservation(string Handle, DateTime FirstSeen, DateTime LastSeen);
    private sealed record HandleWindow(string Handle, DateTime? From, DateTime? Through, DateTime? Before);
    private sealed record GuildInfo(string Name, string? OrgSid);
}
