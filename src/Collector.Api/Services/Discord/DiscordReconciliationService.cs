using System.Globalization;
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Discord;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// A Discord member to reconcile: their id, whether they are a bot, and the RSI rank their
/// Discord rank stands for (the RsiRankLabel of their rank role), when configured.
/// </summary>
public sealed record ReconciliationSubject(string DiscordUserId, bool IsBot, string? RsiRankLabel);

/// <summary>
/// A member's reconciliation (spec § 10.2). <see cref="Status"/> is a
/// <see cref="DiscordReconciliationStatus"/> value, null for a bot or in an unmapped guild.
/// <see cref="Person"/> is the linked person the status is about: the one found in the roster
/// (with a coherent rank first), else the first linked person.
/// </summary>
public sealed record MemberReconciliation(
    string? Status,
    bool MultipleLinks,
    IReadOnlyList<DiscordLinkedPersonDto> Links,
    string? RsiRank,
    DiscordLinkedPersonDto? Person);

/// <summary>
/// Reconciles Discord members with the active RSI roster of the org their guild is mapped to
/// (spec § 10.2), lists the discrepancies of a guild, and the accounts active in several
/// tracked guilds (spec § 10.3). Everything is computed on read from the guild's current
/// mapping: re-mapping a guild re-reconciles it at once. A member is linked through every
/// entity_links "discord" row carrying their id; a linked person is in the roster when an
/// active row carries their citizen id, else (and only then) their handle regardless of case,
/// unless that row belongs to another known citizen.
/// </summary>
public sealed class DiscordReconciliationService(TrackerDbContext db)
{
    private static readonly List<LinkedPerson> NoPersons = [];

    /// <summary>The RSI rank a member's Discord rank stands for, when configured.</summary>
    public static string? RsiRankLabelOf(RankRole? rank, IReadOnlyDictionary<string, DiscordRole> roles)
        => rank is not null && roles.TryGetValue(rank.RoleId, out var role) ? role.RsiRankLabel : null;

    /// <summary>Ranks are compared trimmed and regardless of case; no RSI equivalent configured is always coherent.</summary>
    public static bool RankIsCoherent(string? rsiRankLabel, string? rsiRank)
        => string.IsNullOrWhiteSpace(rsiRankLabel)
           || string.Equals(rsiRankLabel.Trim(), rsiRank?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles <paramref name="subjects"/> against the active roster of
    /// <paramref name="orgSid"/> (null: unmapped guild, no status). Reads the links of the
    /// subjects, then only the roster rows of the people linked to them: a page of members
    /// costs a handful of queries whatever the size of the org.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MemberReconciliation>> ReconcileAsync(
        string? orgSid, IReadOnlyCollection<ReconciliationSubject> subjects, CancellationToken ct)
    {
        if (subjects.Count == 0) return new Dictionary<string, MemberReconciliation>(StringComparer.Ordinal);

        var links = await LoadLinksAsync(subjects.Select(s => s.DiscordUserId), ct);
        RosterIndex? roster = null;
        if (orgSid is not null && subjects.Any(s => !s.IsBot)
            && await db.OrganizationMembers.AnyAsync(m => m.OrgSid == orgSid && m.IsActive, ct))
        {
            roster = new RosterIndex(await LoadRosterOfAsync(orgSid, links.Values.SelectMany(p => p).ToList(), ct));
        }
        return Reconcile(orgSid, subjects, links, roster);
    }

    /// <summary>
    /// Discrepancies and totals of a guild. Unmapped: <c>{ orgSid: null, items: [], totals: null }</c>.
    /// rsi_only items are only computed once a complete sync exists (the only one that proves
    /// absences). Order: not_in_rsi_org, rank_mismatch (by Discord name), then rsi_only (by handle).
    /// </summary>
    public async Task<DiscordDiscrepanciesDto> GetDiscrepanciesAsync(string guildId, CancellationToken ct)
    {
        var guild = await db.DiscordGuilds.AsNoTracking()
                .Where(g => g.GuildId == guildId)
                .Select(g => new { g.OrgSid, g.LastCompleteSyncAt })
                .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Serveur Discord inconnu.");
        if (guild.OrgSid is null)
            return new DiscordDiscrepanciesDto { OrgSid = null, RsiOnlyAvailable = false, Items = [], Totals = null };
        var orgSid = guild.OrgSid;

        var members = await (
                from m in db.DiscordMembers.AsNoTracking()
                join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                where m.GuildId == guildId && m.LeftAt == null && !a.IsBot
                select new { m.DiscordUserId, m.Nick, m.RoleIdsJson, a.Username, a.GlobalName })
            .ToListAsync(ct);
        var roles = await LoadRolesAsync(guildId, ct);
        var rosterRows = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == orgSid && m.IsActive)
            .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
            .ToListAsync(ct);
        var roster = rosterRows.Count == 0 ? null : new RosterIndex(rosterRows);
        var links = await LoadLinksAsync(members.Select(m => m.DiscordUserId), ct);

        var ranks = members.ToDictionary(
            m => m.DiscordUserId,
            m => DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(m.RoleIdsJson), roles),
            StringComparer.Ordinal);
        var statuses = Reconcile(
            orgSid,
            members.Select(m => new ReconciliationSubject(m.DiscordUserId, false, RsiRankLabelOf(ranks[m.DiscordUserId], roles))),
            links,
            roster);

        var items = new List<DiscordDiscrepancyDto>();
        foreach (var member in members)
        {
            var reconciled = statuses[member.DiscordUserId];
            if (reconciled.Status is not (DiscordReconciliationStatus.NotInRsiOrg or DiscordReconciliationStatus.RankMismatch))
                continue;
            items.Add(new DiscordDiscrepancyDto
            {
                Kind = reconciled.Status!,
                Handle = reconciled.Person?.Handle,
                CitizenId = reconciled.Person?.CitizenId,
                DiscordUserId = member.DiscordUserId,
                DiscordName = member.Nick ?? member.GlobalName ?? member.Username,
                DiscordRank = ranks[member.DiscordUserId]?.Name,
                RsiRank = reconciled.RsiRank,
            });
        }

        var rsiOnlyAvailable = guild.LastCompleteSyncAt is not null;
        if (rsiOnlyAvailable && roster is not null)
        {
            // Covered: a roster row one of the active members' linked people matches.
            var covered = new HashSet<RosterRow>(links.Values.SelectMany(p => p).SelectMany(p => roster.Match(p)));
            items.AddRange(roster.Rows
                .Where(r => !covered.Contains(r))
                .DistinctBy(PersonKey)
                .Select(r => new DiscordDiscrepancyDto
                {
                    Kind = DiscordDiscrepancyKinds.RsiOnly, Handle = r.Handle, CitizenId = r.CitizenId, RsiRank = r.Rank,
                }));
        }

        var counts = await db.OrgMemberCounts.AsNoTracking()
            .Where(c => c.OrgSid == orgSid)
            .OrderByDescending(c => c.CollectedAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        var breakdownKnown = counts?.VisibleCount is not null;

        return new DiscordDiscrepanciesDto
        {
            OrgSid = orgSid,
            RsiOnlyAvailable = rsiOnlyAvailable,
            Items = items
                .OrderBy(i => KindOrder(i.Kind))
                .ThenBy(i => i.DiscordName ?? i.Handle ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DiscordUserId ?? i.Handle ?? "", StringComparer.Ordinal)
                .ToList(),
            Totals = new DiscordTotalsDto
            {
                DiscordActive = members.Count,
                DiscordLinked = members.Count(m => links.ContainsKey(m.DiscordUserId)),
                RsiVisible = breakdownKnown ? counts!.VisibleCount : null,
                RsiRedacted = breakdownKnown ? counts!.RedactedCount : null,
                RsiHidden = breakdownKnown ? counts!.HiddenCount : null,
                RsiTotalRows = counts?.TotalRows,
                RsiCountsAt = counts?.CollectedAt,
                RsiBreakdownKnown = breakdownKnown,
            },
        };
    }

    /// <summary>
    /// Non-bot accounts active in at least two tracked guilds, by username, paged by the
    /// database; for each, their active guilds with org and rank, their links, and the union
    /// of the active RSI orgs of every linked person with their rank there.
    /// </summary>
    public async Task<PaginatedResponse<DiscordMultiMemberDto>> GetMultiMembershipAsync(
        int page, int pageSize, CancellationToken ct)
    {
        page = Paging.Page(page);
        pageSize = Paging.PageSize(pageSize);

        var inSeveralGuilds = db.DiscordMembers
            .Where(m => m.LeftAt == null)
            .GroupBy(m => m.DiscordUserId)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key);
        var accountsQuery = db.DiscordAccounts.AsNoTracking()
            .Where(a => !a.IsBot && inSeveralGuilds.Contains(a.DiscordUserId));
        var total = await accountsQuery.CountAsync(ct);
        var accounts = await accountsQuery
            .OrderBy(a => a.Username).ThenBy(a => a.DiscordUserId)
            .Skip(Paging.Offset(page, pageSize))
            .Take(pageSize)
            .Select(a => new { a.DiscordUserId, a.Username, a.GlobalName })
            .ToListAsync(ct);
        if (accounts.Count == 0)
            return PaginatedResponse<DiscordMultiMemberDto>.Create(Array.Empty<DiscordMultiMemberDto>(), page, pageSize, total);

        var ids = accounts.Select(a => a.DiscordUserId).ToList();
        var memberships = await (
                from m in db.DiscordMembers.AsNoTracking()
                join g in db.DiscordGuilds.AsNoTracking() on m.GuildId equals g.GuildId
                where m.LeftAt == null && ids.Contains(m.DiscordUserId)
                select new { m.DiscordUserId, m.GuildId, GuildName = g.Name, g.OrgSid, m.RoleIdsJson })
            .ToListAsync(ct);
        var guildIds = memberships.Select(m => m.GuildId).Distinct(StringComparer.Ordinal).ToList();
        var rolesByGuild = (await db.DiscordRoles.AsNoTracking().Where(r => guildIds.Contains(r.GuildId)).ToListAsync(ct))
            .GroupBy(r => r.GuildId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.RoleId, StringComparer.Ordinal), StringComparer.Ordinal);
        var links = await LoadLinksAsync(ids, ct);
        var persons = links.Values.SelectMany(p => p).DistinctBy(p => p.EntityId).ToList();
        var rosters = (await LoadActiveOrgRowsAsync(persons, ct))
            .GroupBy(r => r.OrgSid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RosterIndex(g))
            .ToList();

        var items = accounts.Select(a =>
        {
            var linked = links.TryGetValue(a.DiscordUserId, out var found) ? found : NoPersons;
            return new DiscordMultiMemberDto
            {
                DiscordUserId = a.DiscordUserId,
                Username = a.Username,
                GlobalName = a.GlobalName,
                Guilds = memberships
                    .Where(m => m.DiscordUserId == a.DiscordUserId)
                    .OrderBy(m => m.GuildName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.GuildId, StringComparer.Ordinal)
                    .Select(m => new DiscordMultiGuildDto
                    {
                        GuildId = m.GuildId,
                        GuildName = m.GuildName,
                        OrgSid = m.OrgSid,
                        Rank = rolesByGuild.TryGetValue(m.GuildId, out var roles)
                            ? DiscordRankResolver.Resolve(DiscordRoleLists.ParseRoleIds(m.RoleIdsJson), roles)?.Name
                            : null,
                    })
                    .ToList(),
                Links = linked.Select(p => p.ToDto()).ToList(),
                RsiOrgs = rosters
                    .Select(roster => linked.SelectMany(p => roster.Match(p)).FirstOrDefault())
                    .OfType<RosterRow>()
                    .OrderBy(r => r.OrgSid, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new DiscordRsiOrgDto { Sid = r.OrgSid, Rank = r.Rank })
                    .ToList(),
            };
        }).ToList();

        return PaginatedResponse<DiscordMultiMemberDto>.Create(items, page, pageSize, total);
    }

    private static Dictionary<string, MemberReconciliation> Reconcile(
        string? orgSid,
        IEnumerable<ReconciliationSubject> subjects,
        IReadOnlyDictionary<string, List<LinkedPerson>> links,
        RosterIndex? roster)
    {
        var result = new Dictionary<string, MemberReconciliation>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            var persons = links.TryGetValue(subject.DiscordUserId, out var linked) ? linked : NoPersons;
            var dtos = persons.Select(p => p.ToDto()).ToList();
            var multiple = persons.Count > 1;

            string? status;
            string? rsiRank = null;
            var about = persons.FirstOrDefault();
            if (orgSid is null || subject.IsBot)
            {
                status = null;
                about = null;
            }
            else if (roster is null)
            {
                status = DiscordReconciliationStatus.RsiUnknown;
            }
            else if (persons.Count == 0)
            {
                status = DiscordReconciliationStatus.Unlinked;
            }
            else
            {
                var matches = persons.SelectMany(p => roster.Match(p).Select(row => (Person: p, Row: row))).ToList();
                if (matches.Count == 0)
                {
                    status = DiscordReconciliationStatus.NotInRsiOrg;
                }
                else
                {
                    var coherent = matches.FirstOrDefault(x => RankIsCoherent(subject.RsiRankLabel, x.Row.Rank));
                    var chosen = coherent.Row is not null ? coherent : matches[0];
                    status = coherent.Row is not null ? DiscordReconciliationStatus.Ok : DiscordReconciliationStatus.RankMismatch;
                    rsiRank = chosen.Row.Rank;
                    about = chosen.Person;
                }
            }
            result[subject.DiscordUserId] = new MemberReconciliation(status, multiple, dtos, rsiRank, about?.ToDto());
        }
        return result;
    }

    /// <summary>The people linked to each Discord id (distinct entities, oldest first).</summary>
    private async Task<Dictionary<string, List<LinkedPerson>>> LoadLinksAsync(
        IEnumerable<string> discordUserIds, CancellationToken ct)
    {
        var ids = discordUserIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return new Dictionary<string, List<LinkedPerson>>(StringComparer.Ordinal);

        var rows = await (
                from l in db.EntityLinks.AsNoTracking()
                join e in db.TrackedEntities.AsNoTracking() on l.TrackedEntityId equals e.Id
                where l.Provider == LinkProviders.Discord && ids.Contains(l.Value)
                select new { l.Value, EntityId = e.Id, e.CitizenId, e.CurrentHandle, e.DisplayName })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.Value, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.DistinctBy(r => r.EntityId)
                    .OrderBy(r => r.EntityId)
                    .Select(r => new LinkedPerson(r.EntityId, r.CitizenId, r.CurrentHandle, r.DisplayName))
                    .ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>Active roster rows of the org that may be one of <paramref name="persons"/>.</summary>
    private async Task<List<RosterRow>> LoadRosterOfAsync(
        string orgSid, IReadOnlyCollection<LinkedPerson> persons, CancellationToken ct)
    {
        var citizenIds = persons.Where(p => p.CitizenId is not null).Select(p => p.CitizenId!.Value).Distinct().ToList();
        var handles = persons.Where(p => p.Handle is not null).Select(p => p.Handle!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (citizenIds.Count == 0 && handles.Count == 0) return [];

        return await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == orgSid && m.IsActive
                && ((m.CitizenId != null && citizenIds.Contains(m.CitizenId.Value))
                    || handles.Contains(EF.Functions.Collate(m.UserHandle, "NOCASE"))))
            .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Active roster rows, in any org, that may be one of <paramref name="persons"/>: by citizen
    /// id, then by handle through the NOCASE index (two queries, each served by an index).
    /// </summary>
    private async Task<List<RosterRow>> LoadActiveOrgRowsAsync(IReadOnlyCollection<LinkedPerson> persons, CancellationToken ct)
    {
        var citizenIds = persons.Where(p => p.CitizenId is not null).Select(p => p.CitizenId!.Value).Distinct().ToList();
        var handles = persons.Where(p => p.Handle is not null).Select(p => p.Handle!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<RosterRow>();
        if (citizenIds.Count > 0)
        {
            rows.AddRange(await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.IsActive && m.CitizenId != null && citizenIds.Contains(m.CitizenId.Value))
                .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
                .ToListAsync(ct));
        }
        if (handles.Count > 0)
        {
            rows.AddRange(await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.IsActive && handles.Contains(EF.Functions.Collate(m.UserHandle, "NOCASE")))
                .Select(m => new RosterRow(m.OrgSid, m.UserHandle, m.CitizenId, m.Rank))
                .ToListAsync(ct));
        }
        return rows.Distinct().ToList();
    }

    private async Task<Dictionary<string, DiscordRole>> LoadRolesAsync(string guildId, CancellationToken ct)
        => (await db.DiscordRoles.AsNoTracking().Where(r => r.GuildId == guildId).ToListAsync(ct))
            .ToDictionary(r => r.RoleId, StringComparer.Ordinal);

    private static string PersonKey(RosterRow row)
        => row.CitizenId is int citizenId
            ? citizenId.ToString(CultureInfo.InvariantCulture)
            : "h:" + row.Handle.ToLowerInvariant();

    private static int KindOrder(string kind) => kind switch
    {
        DiscordDiscrepancyKinds.NotInRsiOrg => 0,
        DiscordDiscrepancyKinds.RankMismatch => 1,
        _ => 2,
    };

    /// <summary>A tracked person behind a discord link.</summary>
    private sealed record LinkedPerson(long EntityId, int? CitizenId, string? Handle, string? DisplayName)
    {
        public DiscordLinkedPersonDto ToDto() => new() { Handle = Handle, CitizenId = CitizenId, DisplayName = DisplayName };
    }

    /// <summary>An active organization_members row, reduced to what reconciliation reads.</summary>
    private sealed record RosterRow(string OrgSid, string Handle, int? CitizenId, string? Rank);

    /// <summary>Active roster rows of one org, by citizen id and by handle regardless of case.</summary>
    private sealed class RosterIndex
    {
        private readonly Dictionary<int, List<RosterRow>> _byCitizen = new();
        private readonly Dictionary<string, List<RosterRow>> _byHandle = new(StringComparer.OrdinalIgnoreCase);

        public RosterIndex(IEnumerable<RosterRow> rows)
        {
            Rows = rows.ToList();
            foreach (var row in Rows)
            {
                if (row.CitizenId is int citizenId) Add(_byCitizen, citizenId, row);
                Add(_byHandle, row.Handle, row);
            }
        }

        public IReadOnlyList<RosterRow> Rows { get; }

        /// <summary>
        /// The rows of <paramref name="person"/>: by citizen id first; else by handle regardless
        /// of case, except rows of another known citizen (the handle was taken again).
        /// </summary>
        public IReadOnlyList<RosterRow> Match(LinkedPerson person)
        {
            if (person.CitizenId is int citizenId && _byCitizen.TryGetValue(citizenId, out var byCitizen)) return byCitizen;
            if (person.Handle is null || !_byHandle.TryGetValue(person.Handle, out var byHandle)) return [];
            return byHandle
                .Where(r => r.CitizenId is null || person.CitizenId is null || r.CitizenId == person.CitizenId)
                .ToList();
        }

        private static void Add<TKey>(Dictionary<TKey, List<RosterRow>> index, TKey key, RosterRow row) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var rows))
            {
                rows = [];
                index[key] = rows;
            }
            rows.Add(row);
        }
    }
}
