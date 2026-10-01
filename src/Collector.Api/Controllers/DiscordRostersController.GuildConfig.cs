using Collector.Api.Dtos.Discord;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Guild configuration: corpo mapping and rank roles (spec § 11). Both answers are read back
/// through <see cref="DiscordRosterQueryService"/>, so they match what the GET routes return.
/// </summary>
public partial class DiscordRostersController
{
    /// <summary>Maps the guild to an org (or unmaps it with null) and returns the guild.</summary>
    [HttpPut("discord/guilds/{guildId}/org")]
    public async Task<ActionResult<DiscordGuildSummaryDto>> MapGuildOrg(
        string guildId, [FromBody] MapDiscordGuildOrgRequest request,
        [FromServices] DiscordGuildConfigService config, [FromServices] DiscordRosterQueryService queries,
        CancellationToken ct)
    {
        await config.MapOrgAsync(guildId, request.OrgSid, ct);
        var guild = await queries.GetGuildAsync(guildId, ct);
        if (guild is null) return NotFound();
        return Ok(guild);
    }

    /// <summary>Configures one role as a rank (or not) and returns it.</summary>
    [HttpPut("discord/guilds/{guildId}/roles/{roleId}")]
    public async Task<ActionResult<DiscordRoleDto>> UpdateGuildRole(
        string guildId, string roleId, [FromBody] UpdateDiscordRoleRequest request,
        [FromServices] DiscordGuildConfigService config, [FromServices] DiscordRosterQueryService queries,
        CancellationToken ct)
    {
        await config.UpdateRoleAsync(guildId, roleId, request, ct);
        var guild = await queries.GetGuildAsync(guildId, ct);
        var role = guild?.Roles.FirstOrDefault(r => r.RoleId == roleId);
        if (role is null) return NotFound();
        return Ok(role);
    }
}
