using Collector.Api.Auth;
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>Query of GET api/discord/guilds/{guildId}/members.</summary>
public sealed record DiscordMemberQuery(
    string? Status, string? Search, string? RankRoleId, string? Reconciliation, int Page, int PageSize);

/// <summary>
/// Reads of the DISCORD tab (spec § 11): guild summaries and detail, members, events and the
/// sync log. Guild summaries aggregate role combinations in SQL and never look a handle up:
/// suggestions are computed only by their own route.
///
/// Members are paged by the database whenever the page can be cut there: status, search and
/// order are SQL, and the rank, links and status of the page's rows only are computed after,
/// in a handful of queries whatever the guild size. A rank or reconciliation filter depends on
/// values computed in memory (the rank from the role configuration, the status from links
/// and the RSI roster), so it takes one bounded pass instead: the rows matching the SQL
/// filters (for a rank, only those holding the role, found by a LIKE on RoleIdsJson) are read
/// as (id, roles, bot) triples in order, ranked, reconciled when asked, and cut in memory;
/// only the page's rows are then read in full. The pass holds at most one small triple per
/// member of the guild (50,000 at most, the ingest bound), plus the links of those members
/// and the roster rows of their linked people, in a constant number of queries.
/// </summary>
public sealed class DiscordRosterQueryService(
    TrackerDbContext db,
    DiscordReconciliationService reconciliation,
    IOrganizationRepository organizations,
    CurrentUserAccessor currentUser)
{
    /// <summary>Default number of events and syncs returned.</summary>
    public const int DefaultLimit = 100;

    private static readonly IReadOnlyDictionary<string, DiscordRole> NoRoles = new Dictionary<string, DiscordRole>();

    /// <summary>Every tracked guild: unmapped ones first, then by name.</summary>
    public async Task<IReadOnlyList<DiscordGuildSummaryDto>> ListGuildsAsync(CancellationToken ct)
    {
        var guilds = await db.DiscordGuilds.AsNoTracking().ToListAsync(ct);
        if (guilds.Count == 0) return [];

        var combos = (await RoleCombosAsync(null, ct)).ToLookup(c => c.GuildId, StringComparer.Ordinal);
        var roles = (await db.DiscordRoles.AsNoTracking().ToListAsync(ct))
            .GroupBy(r => r.GuildId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.RoleId, StringComparer.Ordinal), StringComparer.Ordinal);
        var lastSyncs = await LastSyncsAsync(null, ct);
        var orgNames = await organizations.GetLatestNamesBySidsAsync(
            guilds.Where(g => g.OrgSid is not null).Select(g => g.OrgSid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

        return guilds
            .Select(g =>
            {
                var summary = new DiscordGuildSummaryDto();
                FillSummary(
                    summary,
                    g,
                    roles.TryGetValue(g.GuildId, out var guildRoles) ? guildRoles : NoRoles,
                    combos[g.GuildId],
                    lastSyncs.GetValueOrDefault(g.GuildId),
                    g.OrgSid is null ? null : orgNames.GetValueOrDefault(g.OrgSid));
                return summary;
            })
            .OrderBy(s => s.OrgSid is null ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.GuildId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A guild with all its roles (deleted ones included), its org's RSI ranks and canEdit; null when unknown.</summary>
    public async Task<DiscordGuildDetailDto?> GetGuildAsync(string guildId, CancellationToken ct)
    {
        var guild = await db.DiscordGuilds.AsNoTracking().FirstOrDefaultAsync(g => g.GuildId == guildId, ct);
        if (guild is null) return null;

        var combos = await RoleCombosAsync(guildId, ct);
        var roles = await LoadRolesAsync(guildId, ct);
        var lastSync = (await LastSyncsAsync(guildId, ct)).GetValueOrDefault(guildId);
        string? orgName = null;
        IReadOnlyList<string> rsiRanks = [];
        if (guild.OrgSid is not null)
        {
            orgName = (await organizations.GetLatestNamesBySidsAsync([guild.OrgSid], ct)).GetValueOrDefault(guild.OrgSid);
            rsiRanks = await RsiRanksAsync(guild.OrgSid, ct);
        }

        var detail = new DiscordGuildDetailDto();
        FillSummary(detail, guild, roles, combos, lastSync, orgName);
        var holders = RoleMemberCounts(combos);
        detail.Roles = roles.Values
            .OrderBy(r => r.DeletedAt is null ? 0 : 1)
            .ThenByDescending(r => r.Position)
            .ThenBy(r => r.RoleId, StringComparer.Ordinal)
            .Select(r => new DiscordRoleDto
            {
                RoleId = r.RoleId,
                Name = r.Name,
                Position = r.Position,
                Color = r.Color,
                Hoist = r.Hoist,
                Managed = r.Managed,
                IsRank = r.IsRank,
                RankOrder = r.RankOrder,
                RsiRankLabel = r.RsiRankLabel,
                Deleted = r.DeletedAt is not null,
                MemberCount = holders.GetValueOrDefault(r.RoleId),
            })
            .ToList();
        detail.RsiRanks = rsiRanks;
        // The responsible-user rule of spec § 11 (the same rule guards the configuration routes).
        detail.CanEdit = guild.OrgSid is null
            || currentUser.IsAdmin
            || (guild.OrgMappedByApiUserId is long owner && owner == currentUser.UserId);
        return detail;
    }

    /// <summary>
    /// A page of members (see the class summary for how filters are paged). status: active
    /// (default), former or all, else 400; rankRoleId: a snowflake, else 400; reconciliation: a
    /// <see cref="DiscordReconciliationStatus"/> value, else 400, ignored for an unmapped guild.
    /// </summary>
    public async Task<PaginatedResponse<DiscordMemberDto>> GetMembersAsync(
        string guildId, DiscordMemberQuery query, CancellationToken ct)
    {
        bool? active = (query.Status ?? "active").Trim().ToLowerInvariant() switch
        {
            "active" => true,
            "former" => false,
            "all" => null,
            _ => throw new ValidationException("status doit valoir active, former ou all."),
        };
        var rankRoleId = string.IsNullOrWhiteSpace(query.RankRoleId) ? null : query.RankRoleId.Trim();
        if (rankRoleId is not null && !DiscordSnowflake.IsValid(rankRoleId))
            throw new ValidationException("rankRoleId doit être un identifiant de rôle Discord (17 à 20 chiffres).");
        var wanted = string.IsNullOrWhiteSpace(query.Reconciliation) ? null : query.Reconciliation.Trim().ToLowerInvariant();
        if (wanted is not null && !DiscordReconciliationStatus.IsValid(wanted))
            throw new ValidationException("reconciliation doit valoir rsi_unknown, unlinked, ok, rank_mismatch ou not_in_rsi_org.");
        var page = Paging.Page(query.Page);
        var pageSize = Paging.PageSize(query.PageSize);

        var guild = await db.DiscordGuilds.AsNoTracking()
                .Where(g => g.GuildId == guildId)
                .Select(g => new { g.OrgSid })
                .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Serveur Discord inconnu.");
        // Spec § 10.2: members of an unmapped guild have no status, so the filter is ignored.
        if (guild.OrgSid is null) wanted = null;
        var roles = await LoadRolesAsync(guildId, ct);

        var rows = from m in db.DiscordMembers.AsNoTracking()
                   join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                   where m.GuildId == guildId
                   select new { Member = m, Account = a };
        if (active == true) rows = rows.Where(r => r.Member.LeftAt == null);
        if (active == false) rows = rows.Where(r => r.Member.LeftAt != null);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // LIKE is case-insensitive for ASCII; the user's wildcards are escaped.
            var escaped = query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = $"%{escaped}%";
            rows = rows.Where(r =>
                EF.Functions.Like(r.Account.Username, pattern, "\\")
                || (r.Account.GlobalName != null && EF.Functions.Like(r.Account.GlobalName, pattern, "\\"))
                || (r.Member.Nick != null && EF.Functions.Like(r.Member.Nick, pattern, "\\")));
        }
        if (rankRoleId is not null)
        {
            // Necessary condition, decided by SQL: the member holds the role (a snowflake, so no
            // wildcard). Whether it is their rank depends on their other roles: decided below.
            var holds = $"%\"{rankRoleId}\"%";
            rows = rows.Where(r => EF.Functions.Like(r.Member.RoleIdsJson, holds));
        }
        var ordered = rows
            .OrderBy(r => EF.Functions.Collate(r.Member.Nick ?? r.Account.GlobalName ?? r.Account.Username, "NOCASE"))
            .ThenBy(r => r.Member.DiscordUserId);

        if (rankRoleId is null && wanted is null)
        {
            var total = await rows.CountAsync(ct);
            var pageRows = await ordered.Skip(Paging.Offset(page, pageSize)).Take(pageSize).ToListAsync(ct);
            var ranks = pageRows.ToDictionary(r => r.Member.DiscordUserId, r => Rank(r.Member.RoleIdsJson, roles), StringComparer.Ordinal);
            var statuses = await reconciliation.ReconcileAsync(
                guild.OrgSid,
                pageRows.Select(r => new ReconciliationSubject(
                    r.Member.DiscordUserId, r.Account.IsBot,
                    DiscordReconciliationService.RsiRankLabelOf(ranks[r.Member.DiscordUserId], roles))).ToList(),
                ct);
            return PaginatedResponse<DiscordMemberDto>.Create(
                pageRows.Select(r => ToMemberDto(r.Member, r.Account, roles, ranks[r.Member.DiscordUserId], statuses[r.Member.DiscordUserId]))
                    .ToList(),
                page, pageSize, total);
        }

        // Bounded in-memory pass (class summary).
        var candidates = await ordered
            .Select(r => new { r.Member.DiscordUserId, r.Member.RoleIdsJson, r.Account.IsBot })
            .ToListAsync(ct);
        var ranked = candidates
            .Select(c => (c.DiscordUserId, c.IsBot, Rank: Rank(c.RoleIdsJson, roles)))
            .Where(c => rankRoleId is null || c.Rank?.RoleId == rankRoleId)
            .ToList();
        IReadOnlyDictionary<string, MemberReconciliation>? statusesOfAll = null;
        if (wanted is not null)
        {
            var all = await reconciliation.ReconcileAsync(
                guild.OrgSid,
                ranked.Select(c => new ReconciliationSubject(
                    c.DiscordUserId, c.IsBot, DiscordReconciliationService.RsiRankLabelOf(c.Rank, roles))).ToList(),
                ct);
            statusesOfAll = all;
            ranked = ranked.Where(c => all[c.DiscordUserId].Status == wanted).ToList();
        }

        var pageOfMembers = ranked.Skip(Paging.Offset(page, pageSize)).Take(pageSize).ToList();
        var ids = pageOfMembers.Select(c => c.DiscordUserId).ToList();
        var loaded = (await rows.Where(r => ids.Contains(r.Member.DiscordUserId)).ToListAsync(ct))
            .ToDictionary(r => r.Member.DiscordUserId, StringComparer.Ordinal);
        var pageStatuses = statusesOfAll ?? await reconciliation.ReconcileAsync(
            guild.OrgSid,
            pageOfMembers.Select(c => new ReconciliationSubject(
                c.DiscordUserId, c.IsBot, DiscordReconciliationService.RsiRankLabelOf(c.Rank, roles))).ToList(),
            ct);
        var items = pageOfMembers
            .Where(c => loaded.ContainsKey(c.DiscordUserId))
            .Select(c =>
            {
                var row = loaded[c.DiscordUserId];
                return ToMemberDto(row.Member, row.Account, roles, c.Rank, pageStatuses[c.DiscordUserId]);
            })
            .ToList();
        return PaginatedResponse<DiscordMemberDto>.Create(items, page, pageSize, ranked.Count);
    }

    /// <summary>
    /// The guild's history, newest Id first: its own events and the account-level events
    /// (renames, GuildId null) of accounts that are or were its members. type and userId filter
    /// both; limit is bounded by <see cref="Paging.Limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<DiscordEventDto>> GetEventsAsync(
        string guildId, string? type, string? userId, int limit, CancellationToken ct)
    {
        limit = Paging.Limit(limit);
        await EnsureGuildAsync(guildId, ct);

        var visibleEvents = db.DiscordMemberEvents.AsNoTracking()
            .Where(e => !db.DiscordSyncs.Any(s => s.Id == e.SyncId && s.EventCount < 0));
        var guildEvents = visibleEvents.Where(e => e.GuildId == guildId);
        var memberIds = db.DiscordMembers.Where(m => m.GuildId == guildId).Select(m => m.DiscordUserId);
        var accountEvents = visibleEvents
            .Where(e => e.GuildId == null && memberIds.Contains(e.DiscordUserId));
        if (!string.IsNullOrWhiteSpace(type))
        {
            var wantedType = type.Trim();
            guildEvents = guildEvents.Where(e => e.Type == wantedType);
            accountEvents = accountEvents.Where(e => e.Type == wantedType);
        }
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var wantedUser = userId.Trim();
            guildEvents = guildEvents.Where(e => e.DiscordUserId == wantedUser);
            accountEvents = accountEvents.Where(e => e.DiscordUserId == wantedUser);
        }

        // Two index-served queries rather than one OR, then merged in Id order.
        var events = (await guildEvents.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct))
            .Concat(await accountEvents.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct))
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .ToList();
        if (events.Count == 0) return [];

        var userIds = events.Select(e => e.DiscordUserId).Distinct(StringComparer.Ordinal).ToList();
        var usernames = await db.DiscordAccounts.AsNoTracking()
            .Where(a => userIds.Contains(a.DiscordUserId))
            .ToDictionaryAsync(a => a.DiscordUserId, a => a.Username, StringComparer.Ordinal, ct);
        var syncIds = events.Select(e => e.SyncId).Distinct().ToList();
        var senders = await db.DiscordSyncs.AsNoTracking()
            .Where(s => syncIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.SubmittedByUsername, ct);
        var roles = await LoadRolesAsync(guildId, ct);

        return events.Select(e => new DiscordEventDto
        {
            Id = e.Id,
            GuildId = e.GuildId,
            DiscordUserId = e.DiscordUserId,
            Username = usernames.GetValueOrDefault(e.DiscordUserId),
            Type = e.Type,
            OldValue = e.OldValue,
            NewValue = e.NewValue,
            OccurredAt = e.OccurredAt,
            NotBefore = e.NotBefore,
            ObservedAt = e.ObservedAt,
            SubmittedBy = senders.GetValueOrDefault(e.SyncId),
            RankChange = e.Type == DiscordEventTypes.RolesChanged ? RankChange(e, roles) : null,
        }).ToList();
    }

    /// <summary>The guild's sync log, newest first, bounded by <see cref="Paging.Limit"/>.</summary>
    public async Task<IReadOnlyList<DiscordSyncDto>> GetSyncsAsync(string guildId, int limit, CancellationToken ct)
    {
        limit = Paging.Limit(limit);
        await EnsureGuildAsync(guildId, ct);
        return await db.DiscordSyncs.AsNoTracking()
            .Where(s => s.GuildId == guildId && s.EventCount >= 0)
            .OrderByDescending(s => s.Id)
            .Take(limit)
            .Select(s => new DiscordSyncDto
            {
                Id = s.Id,
                ReceivedAt = s.ReceivedAt,
                CollectedAt = s.CollectedAt,
                SubmittedBy = s.SubmittedByUsername,
                Method = s.Method,
                DeclaredComplete = s.DeclaredComplete,
                IsComplete = s.IsComplete,
                IsBaseline = s.IsBaseline,
                MassDepartureDetected = s.MassDepartureDetected,
                ExpectedCount = s.ExpectedCount,
                CollectedCount = s.CollectedCount,
                OptedOutCount = s.OptedOutCount,
                UnknownRoleRefCount = s.UnknownRoleRefCount,
                EventCount = s.EventCount,
                PluginVersion = s.PluginVersion,
            })
            .ToListAsync(ct);
    }

    private static void FillSummary(
        DiscordGuildSummaryDto dto, DiscordGuild guild, IReadOnlyDictionary<string, DiscordRole> roles,
        IEnumerable<RoleCombo> combos, DiscordSync? lastSync, string? orgName)
    {
        var active = 0;
        var perRank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var combo in combos)
        {
            active += combo.Count;
            if (Rank(combo.RoleIdsJson, roles) is { } rank)
                perRank[rank.RoleId] = perRank.GetValueOrDefault(rank.RoleId) + combo.Count;
        }
        var ranks = perRank.Keys.Select(id => roles[id]).ToList();
        ranks.Sort(DiscordRankResolver.Compare);

        dto.GuildId = guild.GuildId;
        dto.Name = guild.Name;
        dto.IconHash = guild.IconHash;
        dto.OrgSid = guild.OrgSid;
        dto.OrgName = orgName;
        dto.OrgMappedBy = guild.OrgMappedByUsername;
        dto.ActiveMembers = active;
        dto.RankDistribution = ranks
            .Select(r => new DiscordRankCountDto { RoleId = r.RoleId, Name = r.Name, Color = r.Color, Count = perRank[r.RoleId] })
            .ToList();
        dto.LastSync = lastSync is null
            ? null
            : new DiscordLastSyncDto
            {
                ReceivedAt = lastSync.ReceivedAt,
                IsComplete = lastSync.IsComplete,
                Method = lastSync.Method,
                SubmittedBy = lastSync.SubmittedByUsername,
                MassDepartureDetected = lastSync.MassDepartureDetected,
            };
        dto.LastCompleteSyncAt = guild.LastCompleteSyncAt;
    }

    /// <summary>Active, non-bot members grouped by (guild, role list): a few rows per guild, however many members.</summary>
    private async Task<List<RoleCombo>> RoleCombosAsync(string? guildId, CancellationToken ct)
    {
        var humans = from m in db.DiscordMembers.AsNoTracking()
                     join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                     where m.LeftAt == null && !a.IsBot
                     select m;
        if (guildId is not null) humans = humans.Where(m => m.GuildId == guildId);
        return await humans
            .GroupBy(m => new { m.GuildId, m.RoleIdsJson })
            .Select(g => new RoleCombo(g.Key.GuildId, g.Key.RoleIdsJson, g.Count()))
            .ToListAsync(ct);
    }

    private async Task<Dictionary<string, DiscordSync>> LastSyncsAsync(string? guildId, CancellationToken ct)
    {
        var syncs = db.DiscordSyncs.AsNoTracking().Where(s => s.EventCount >= 0);
        if (guildId is not null) syncs = syncs.Where(s => s.GuildId == guildId);
        var lastIds = syncs.GroupBy(s => s.GuildId).Select(g => g.Max(s => s.Id));
        return await db.DiscordSyncs.AsNoTracking()
            .Where(s => lastIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.GuildId, StringComparer.Ordinal, ct);
    }

    /// <summary>Distinct trimmed ranks of the org's active roster (case-insensitive), highest stars first.</summary>
    private async Task<IReadOnlyList<string>> RsiRanksAsync(string orgSid, CancellationToken ct)
    {
        var rows = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == orgSid && m.IsActive && m.Rank != null)
            .Select(m => new { m.Rank, m.Stars })
            .Distinct()
            .ToListAsync(ct);
        return rows
            .Select(r => new { Rank = r.Rank!.Trim(), r.Stars })
            .Where(r => r.Rank.Length > 0)
            .GroupBy(r => r.Rank, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Rank = g.Select(r => r.Rank).OrderBy(r => r, StringComparer.Ordinal).First(),
                Stars = g.Max(r => r.Stars),
            })
            .OrderByDescending(r => r.Stars ?? -1)
            .ThenBy(r => r.Rank, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Rank)
            .ToList();
    }

    private async Task<Dictionary<string, DiscordRole>> LoadRolesAsync(string guildId, CancellationToken ct)
        => (await db.DiscordRoles.AsNoTracking().Where(r => r.GuildId == guildId).ToListAsync(ct))
            .ToDictionary(r => r.RoleId, StringComparer.Ordinal);

    private async Task EnsureGuildAsync(string guildId, CancellationToken ct)
    {
        if (!await db.DiscordGuilds.AnyAsync(g => g.GuildId == guildId, ct))
            throw new NotFoundException("Serveur Discord inconnu.");
    }

    private static RankRole? Rank(string roleIdsJson, IReadOnlyDictionary<string, DiscordRole> roles)
        => DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(roleIdsJson), roles);

    private static Dictionary<string, int> RoleMemberCounts(IEnumerable<RoleCombo> combos)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var combo in combos)
        {
            foreach (var roleId in DiscordRoleLists.ParseRoleIds(combo.RoleIdsJson).Distinct(StringComparer.Ordinal))
                counts[roleId] = counts.GetValueOrDefault(roleId) + combo.Count;
        }
        return counts;
    }

    /// <summary>
    /// The rank change of a roles_changed event, resolved with the current rank configuration
    /// (spec § 9.5) and named as the event stored the roles then. Null when the rank is the same.
    /// </summary>
    private static DiscordRankChangeDto? RankChange(DiscordMemberEvent e, IReadOnlyDictionary<string, DiscordRole> roles)
    {
        var before = DiscordRoleLists.ParseEventRoles(e.OldValue);
        var after = DiscordRoleLists.ParseEventRoles(e.NewValue);
        var from = DiscordRankResolver.Resolve(before.Select(r => r.Id), roles);
        var to = DiscordRankResolver.Resolve(after.Select(r => r.Id), roles);
        if (from?.RoleId == to?.RoleId) return null;
        return new DiscordRankChangeDto { From = NameThen(from, before), To = NameThen(to, after) };
    }

    private static string? NameThen(RankRole? rank, IReadOnlyList<EventRole> stored)
    {
        if (rank is null) return null;
        var then = stored.FirstOrDefault(r => r.Id == rank.RoleId)?.Name;
        return string.IsNullOrEmpty(then) ? rank.Name : then;
    }

    private static DiscordMemberDto ToMemberDto(
        DiscordMember member, DiscordAccount account, IReadOnlyDictionary<string, DiscordRole> roles,
        RankRole? rank, MemberReconciliation reconciled) => new()
    {
        DiscordUserId = member.DiscordUserId,
        Username = account.Username,
        GlobalName = account.GlobalName,
        Nick = member.Nick,
        IsBot = account.IsBot,
        JoinedAt = member.JoinedAt,
        FirstSeenAt = member.FirstSeenAt,
        LastSeenAt = member.LastSeenAt,
        LeftAt = member.LeftAt,
        Rank = rank is null ? null : new DiscordRankDto { RoleId = rank.RoleId, Name = rank.Name, Color = rank.Color },
        Roles = DiscordRoleLists.ParseRoleIds(member.RoleIdsJson)
            .Distinct(StringComparer.Ordinal)
            .Select(id => roles.TryGetValue(id, out var role) ? role : null)
            .OfType<DiscordRole>()
            .Where(r => r.DeletedAt is null)
            .OrderByDescending(r => r.Position)
            .ThenBy(r => r.RoleId, StringComparer.Ordinal)
            .Select(r => new DiscordRankDto { RoleId = r.RoleId, Name = r.Name, Color = r.Color })
            .ToList(),
        Links = reconciled.Links,
        RsiRank = reconciled.RsiRank,
        Reconciliation = reconciled.Status,
        MultipleLinks = reconciled.MultipleLinks,
    };

    private sealed record RoleCombo(string GuildId, string RoleIdsJson, int Count);
}
