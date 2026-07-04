using Collector.Api.Dtos.Discord;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>Resolves a public Discord profile from a numeric user id (bot-token backed).</summary>
[ApiController]
[Route("api")]
[Authorize]
public class DiscordController : ControllerBase
{
    private readonly DiscordClient _discord;

    public DiscordController(DiscordClient discord) => _discord = discord;

    [HttpGet("discord/users/{id}")]
    public async Task<ActionResult<DiscordUserDto>> GetUser(string id, CancellationToken ct)
    {
        var user = await _discord.GetUserAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }
}
