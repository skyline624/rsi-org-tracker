using Collector.Api.Dtos.Stats;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/stats")]
[Authorize]
public class StatsController : ControllerBase
{
    private readonly StatsService _statsService;

    public StatsController(StatsService statsService)
    {
        _statsService = statsService;
    }

    [HttpGet]
    public async Task<ActionResult<StatsOverviewDto>> GetOverview(CancellationToken ct) =>
        Ok(await _statsService.GetOverviewAsync(ct));

    [HttpGet("timeline")]
    public async Task<ActionResult<IReadOnlyList<TimelinePointDto>>> GetTimeline(
        [FromQuery] int days = 30, CancellationToken ct = default) =>
        Ok(await _statsService.GetTimelineAsync(days, ct));

    [HttpGet("organizations/top")]
    public async Task<ActionResult<IReadOnlyList<OrganizationTopDto>>> GetTopOrganizations(
        [FromQuery] int limit = 10, CancellationToken ct = default) =>
        Ok(await _statsService.GetTopOrganizationsAsync(limit, ct));

    [HttpGet("organizations/archetypes")]
    public async Task<ActionResult<IReadOnlyList<ArchetypeStatsDto>>> GetArchetypes(CancellationToken ct) =>
        Ok(await _statsService.GetArchetypesAsync(ct));

    [HttpGet("members/activity")]
    public async Task<ActionResult<IReadOnlyList<MemberActivityDto>>> GetMemberActivity(
        [FromQuery] int days = 30, CancellationToken ct = default) =>
        Ok(await _statsService.GetMemberActivityAsync(days, ct));
}
