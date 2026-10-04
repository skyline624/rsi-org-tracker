using Collector.Api.Auth;
using Collector.Api.Dtos.Bot;
using Collector.Api.Errors;
using Collector.Api.Services;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

/// <summary>
/// Read routes of the Liberastra Discord bot, reached from outside through nginx
/// /bot-api/ (panda's IP only) with a bot:read key (spec 2026-10-03, § 5).
/// </summary>
[ApiController]
[Route("api/bot")]
[Authorize(Policy = BotReadAuth.PolicyName)]
[ServiceFilter(typeof(BotRequestFilter))]
public sealed class BotController(
    TrackerDbContext db,
    UserLookupService users,
    OrganizationLookupService orgs,
    IOrganizationMemberRepository members,
    IUserHandleHistoryRepository handleHistory,
    IChangeEventRepository changes) : ControllerBase
{
    public const int SearchMax = 10;
    public const int MembersPageSize = 25;
    public const int MaxMovements = 50;
    public const int HistoryEvents = 15;

    [HttpGet("search")]
    public async Task<ActionResult<BotSearchDto>> Search([FromQuery] string? q, CancellationToken ct)
    {
        var text = (q ?? "").Trim();
        if (text.Length is < 2 or > 50)
            throw new ValidationException("La recherche fait de 2 à 50 caractères.");

        var orgHits = await orgs.SuggestAsync(text, ct);
        var sids = orgHits.Select(o => o.Sid).ToList();
        var counts = (await orgs.LatestOrgs().AsNoTracking()
                .Where(o => sids.Contains(o.Sid))
                .Select(o => new { o.Sid, o.MembersCount })
                .ToListAsync(ct))
            .DistinctBy(o => o.Sid)
            .ToDictionary(o => o.Sid, o => o.MembersCount);
        var players = await users.SearchAsync(text, 1, SearchMax, ct);

        return Ok(new BotSearchDto(
            orgHits.Select(o => new BotOrgHitDto(o.Sid, o.Name, counts.GetValueOrDefault(o.Sid))).ToList(),
            players.Items.Select(p => new BotPlayerHitDto(p.UserHandle, p.DisplayName)).ToList()));
    }

    [HttpGet("players/{handle}")]
    public async Task<ActionResult<BotPlayerDto>> Player(string handle, CancellationToken ct)
    {
        var resolved = await users.ResolveHandleAsync(handle, ct)
            ?? throw new NotFoundException($"Joueur « {handle} » inconnu du tracker.");
        var user = await LatestUserAsync(resolved, ct);
        var rows = await users.GetMembershipsAsync(resolved, includeInactive: true, ct);
        if (user is null && rows.Count == 0)
            throw new NotFoundException($"Joueur « {handle} » inconnu du tracker.");

        var latestRow = rows.MaxBy(r => r.Latest.Timestamp);
        return Ok(new BotPlayerDto(
            resolved,
            user?.DisplayName ?? latestRow?.Latest.DisplayName,
            user?.CitizenId,
            user?.Enlisted,
            user?.Location,
            ProfileRead: user is not null,
            rows.Where(r => r.Latest.IsActive).OrderBy(r => r.Latest.OrgSid).Select(ToMembership).ToList(),
            latestRow?.Latest.Timestamp));
    }

    [HttpGet("players/{handle}/history")]
    public async Task<ActionResult<BotHistoryDto>> History(string handle, CancellationToken ct)
    {
        var resolved = await users.ResolveHandleAsync(handle, ct)
            ?? throw new NotFoundException($"Joueur « {handle} » inconnu du tracker.");
        var rows = await users.GetMembershipsAsync(resolved, includeInactive: true, ct);

        var citizenId = (await LatestUserAsync(resolved, ct))?.CitizenId
            ?? (await handleHistory.GetByHandleAsync(resolved, ct))?.CitizenId;
        var handles = citizenId is null
            ? new List<BotHandleDto>()
            : (await handleHistory.GetByCitizenIdAsync(citizenId.Value, ct))
                .Select(h => new BotHandleDto(h.UserHandle, h.FirstSeen, h.LastSeen)).ToList();
        var events = (await changes.GetByUserHandleAsync(resolved, HistoryEvents, ct))
            .Select(e => new BotEventDto(e.Timestamp, e.ChangeType, e.OrgSid, e.OldValue, e.NewValue)).ToList();

        return Ok(new BotHistoryDto(
            resolved,
            rows.OrderByDescending(r => r.Latest.IsActive).ThenByDescending(r => r.Latest.Timestamp).ThenBy(r => r.Latest.OrgSid)
                .Select(ToMembership).ToList(),
            handles,
            events));
    }

    [HttpGet("orgs/{sid}")]
    public async Task<ActionResult<BotOrgDto>> Org(string sid, CancellationToken ct)
    {
        sid = NormalizeSid(sid);
        var org = await db.Organizations.AsNoTracking()
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Organisation « {sid} » inconnue du tracker.");

        var latest = await db.OrgMemberCounts.AsNoTracking()
            .Where(c => c.OrgSid == sid)
            .OrderByDescending(c => c.CollectedAt)
            .FirstOrDefaultAsync(ct);
        BotTrendDto? trend = null;
        if (latest != null)
        {
            // RSI's total 30 days ago: the last count at that date, else the oldest one.
            var monthAgo = DateTime.UtcNow.AddDays(-30);
            var baseline = await db.OrgMemberCounts.AsNoTracking()
                    .Where(c => c.OrgSid == sid && c.CollectedAt <= monthAgo)
                    .OrderByDescending(c => c.CollectedAt)
                    .FirstOrDefaultAsync(ct)
                ?? await db.OrgMemberCounts.AsNoTracking()
                    .Where(c => c.OrgSid == sid)
                    .OrderBy(c => c.CollectedAt)
                    .FirstAsync(ct);
            trend = new BotTrendDto(baseline.TotalRows, latest.TotalRows);
        }
        var readAt = await db.DiscoveredOrganizations.AsNoTracking()
            .Where(d => d.Sid == sid)
            .Select(d => d.LastMembersCollectedAt)
            .FirstOrDefaultAsync(ct);

        return Ok(new BotOrgDto(
            org.Sid, org.Name, org.Archetype, org.Lang, org.Recruiting, org.Roleplay, org.MembersCount,
            latest is null ? null : new BotCountsDto(latest.TotalRows, latest.VisibleCount, latest.RedactedCount, latest.HiddenCount, latest.CollectedAt),
            trend,
            readAt));
    }

    [HttpGet("orgs/{sid}/members")]
    public async Task<ActionResult<BotMembersPageDto>> Members(string sid, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (page < 1)
            throw new ValidationException("La page commence à 1.");
        sid = await KnownSidAsync(sid, ct);
        var (items, total) = await members.GetLatestPageAsync(sid, true, page, MembersPageSize, ct);

        // "Since": first appearance of each member of the page in this org.
        var handles = items.Select(i => i.UserHandle).ToList();
        var since = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == sid && handles.Contains(m.UserHandle))
            .GroupBy(m => m.UserHandle)
            .Select(g => new { Handle = g.Key, First = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.Handle, x => x.First, ct);

        return Ok(new BotMembersPageDto(sid, page, MembersPageSize, total, items
            .Select(m => new BotMemberDto(m.UserHandle, m.DisplayName, m.Rank, m.Stars,
                since.TryGetValue(m.UserHandle, out var first) ? first : null))
            .ToList()));
    }

    [HttpGet("orgs/{sid}/movements")]
    public async Task<ActionResult<BotMovementsDto>> Movements(string sid, [FromQuery] int days = 7, CancellationToken ct = default)
    {
        if (days is < 1 or > 90)
            throw new ValidationException("Le nombre de jours va de 1 à 90.");
        sid = await KnownSidAsync(sid, ct);
        var since = DateTime.UtcNow.AddDays(-days);

        async Task<List<BotMovementDto>> ReadAsync(string type) =>
            (await ChangeEventRepository.MovementsQuery(db.ChangeEvents.AsNoTracking(), sid, since, type)
                .Take(MaxMovements + 1)
                .ToListAsync(ct))
            .Select(e => new BotMovementDto(e.UserHandle ?? e.EntityId, e.Timestamp))
            .ToList();

        var joined = await ReadAsync("member_joined");
        var left = await ReadAsync("member_left");
        var truncated = joined.Count > MaxMovements || left.Count > MaxMovements;
        return Ok(new BotMovementsDto(sid, days, joined.Take(MaxMovements).ToList(), left.Take(MaxMovements).ToList(), truncated));
    }

    /// <summary>
    /// The users row holding a handle. Several rows can share one (a handle given up and taken
    /// by another citizen): the one whose profile was read last holds it now. Player and History
    /// both go through here so they always speak of the same citizen.
    /// </summary>
    private Task<User?> LatestUserAsync(string handle, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(u => u.UserHandle == handle)
            .OrderByDescending(u => u.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    private static string NormalizeSid(string sid)
    {
        var normalized = sid.Trim().ToUpperInvariant();
        if (normalized.Length == 0) throw new ValidationException("SID vide.");
        return normalized;
    }

    private async Task<string> KnownSidAsync(string sid, CancellationToken ct)
    {
        var normalized = NormalizeSid(sid);
        if (!await db.Organizations.AsNoTracking().AnyAsync(o => o.Sid == normalized, ct))
            throw new NotFoundException($"Organisation « {normalized} » inconnue du tracker.");
        return normalized;
    }

    private static BotMembershipDto ToMembership(MembershipRow r) => new(
        r.Latest.OrgSid, r.OrgName, r.Latest.Rank, r.Latest.Stars, r.FirstSeen, r.Latest.Timestamp, r.Latest.IsActive);
}
