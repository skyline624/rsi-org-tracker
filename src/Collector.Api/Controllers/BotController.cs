using Collector.Api.Auth;
using Collector.Api.Dtos.Bot;
using Collector.Api.Errors;
using Collector.Api.Services;
using Collector.Data;
using Collector.Data.Repositories;
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
        var user = await db.Users.AsNoTracking()
            .Where(u => u.UserHandle == resolved)
            .OrderByDescending(u => u.UpdatedAt)
            .FirstOrDefaultAsync(ct);
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

        var citizenId = await db.Users.AsNoTracking()
            .Where(u => u.UserHandle == resolved)
            .Select(u => (int?)u.CitizenId)
            .FirstOrDefaultAsync(ct)
            ?? (await handleHistory.GetByHandleAsync(resolved, ct))?.CitizenId;
        var handles = citizenId is null
            ? new List<BotHandleDto>()
            : (await handleHistory.GetByCitizenIdAsync(citizenId.Value, ct))
                .Select(h => new BotHandleDto(h.UserHandle, h.FirstSeen, h.LastSeen)).ToList();
        var events = (await changes.GetByUserHandleAsync(resolved, HistoryEvents, ct))
            .Select(e => new BotEventDto(e.Timestamp, e.ChangeType, e.OrgSid, e.OldValue, e.NewValue)).ToList();

        return Ok(new BotHistoryDto(
            resolved,
            rows.OrderByDescending(r => r.Latest.IsActive).ThenByDescending(r => r.Latest.Timestamp).Select(ToMembership).ToList(),
            handles,
            events));
    }

    private static BotMembershipDto ToMembership(MembershipRow r) => new(
        r.Latest.OrgSid, r.OrgName, r.Latest.Rank, r.Latest.Stars, r.FirstSeen, r.Latest.Timestamp, r.Latest.IsActive);
}
