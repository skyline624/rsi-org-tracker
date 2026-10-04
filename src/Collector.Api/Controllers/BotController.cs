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
}
