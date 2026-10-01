using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Reads and edits over the Discord rosters sent by the Vencord plugin (spec § 11). Every
/// route sits under the default policy (Smart scheme, signed-in user), so a discord:ingest
/// key is refused here. Sub-paths are complete, as in DiscordController, because the routes
/// span api/discord, api/users and api/organizations. Actions take their services with
/// [FromServices]: the routes are spread over several services.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public partial class DiscordRostersController : ControllerBase
{
    /// <summary>RSI discrepancies and totals of a guild (spec § 10.2).</summary>
    [HttpGet("discord/guilds/{guildId}/discrepancies")]
    public async Task<ActionResult<DiscordDiscrepanciesDto>> GetDiscrepancies(
        string guildId, [FromServices] DiscordReconciliationService reconciliation, CancellationToken ct)
        => Ok(await reconciliation.GetDiscrepanciesAsync(guildId, ct));

    /// <summary>Accounts active in at least two tracked guilds (spec § 10.3).</summary>
    [HttpGet("discord/multi")]
    public async Task<ActionResult<PaginatedResponse<DiscordMultiMemberDto>>> GetMultiMembership(
        [FromServices] DiscordReconciliationService reconciliation,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await reconciliation.GetMultiMembershipAsync(page, pageSize, ct));

    /// <summary>Tracked guilds, unmapped ones first, then by name. Never computes suggestions.</summary>
    [HttpGet("discord/guilds")]
    public async Task<ActionResult<IReadOnlyList<DiscordGuildSummaryDto>>> ListGuilds(
        [FromServices] DiscordRosterQueryService queries, CancellationToken ct)
        => Ok(await queries.ListGuildsAsync(ct));

    /// <summary>A guild with its roles, its org's RSI ranks and whether the caller may configure it.</summary>
    [HttpGet("discord/guilds/{guildId}")]
    public async Task<ActionResult<DiscordGuildDetailDto>> GetGuild(
        string guildId, [FromServices] DiscordRosterQueryService queries, CancellationToken ct)
        => Ok(await queries.GetGuildAsync(guildId, ct) ?? throw new NotFoundException("Serveur Discord inconnu."));

    /// <summary>A page of the guild's members with rank, links and reconciliation status.</summary>
    [HttpGet("discord/guilds/{guildId}/members")]
    public async Task<ActionResult<PaginatedResponse<DiscordMemberDto>>> GetGuildMembers(
        string guildId,
        [FromServices] DiscordRosterQueryService queries,
        [FromQuery] string? status = "active",
        [FromQuery] string? search = null,
        [FromQuery] string? rankRoleId = null,
        [FromQuery] string? reconciliation = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await queries.GetMembersAsync(
            guildId, new DiscordMemberQuery(status, search, rankRoleId, reconciliation, page, pageSize), ct));

    /// <summary>The guild's history, newest first, with the sender of each source sync.</summary>
    [HttpGet("discord/guilds/{guildId}/events")]
    public async Task<ActionResult<IReadOnlyList<DiscordEventDto>>> GetGuildEvents(
        string guildId,
        [FromServices] DiscordRosterQueryService queries,
        [FromQuery] string? type = null,
        [FromQuery] string? userId = null,
        [FromQuery] int limit = DiscordRosterQueryService.DefaultLimit,
        CancellationToken ct = default)
        => Ok(await queries.GetEventsAsync(guildId, type, userId, limit, ct));

    /// <summary>The guild's sync log, newest first.</summary>
    [HttpGet("discord/guilds/{guildId}/syncs")]
    public async Task<ActionResult<IReadOnlyList<DiscordSyncDto>>> GetGuildSyncs(
        string guildId,
        [FromServices] DiscordRosterQueryService queries,
        [FromQuery] int limit = DiscordRosterQueryService.DefaultLimit,
        CancellationToken ct = default)
        => Ok(await queries.GetSyncsAsync(guildId, limit, ct));

    /// <summary>The person's linked Discord accounts, their guilds and the combined timeline (spec § 10.4).</summary>
    [HttpGet("users/{handle}/discord")]
    public async Task<ActionResult<DiscordUserProfileDto>> GetUserDiscord(
        string handle, [FromServices] DiscordProfileService profiles, CancellationToken ct)
        => Ok(await profiles.GetUserProfileAsync(handle, ct));

    /// <summary>The guilds mapped to the org; an empty list when there is none.</summary>
    [HttpGet("organizations/{sid}/discord")]
    public async Task<ActionResult<IReadOnlyList<DiscordOrgGuildDto>>> GetOrgDiscordGuilds(
        string sid, [FromServices] DiscordProfileService profiles, CancellationToken ct)
        => Ok(await profiles.GetOrgGuildsAsync(sid, ct));
}
