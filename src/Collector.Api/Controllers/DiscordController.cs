using Collector.Api.Dtos.Discord;
using Collector.Api.Options;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Collector.Api.Controllers;

/// <summary>
/// Resolves a public Discord profile from a numeric user id (bot-token backed), and tells
/// signed-in users what to enter in the Vencord plugin that uploads Discord rosters.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class DiscordController : ControllerBase
{
    private readonly DiscordClient _discord;
    private readonly DiscordOptions.IngestOptions _ingest;

    public DiscordController(DiscordClient discord, IOptions<DiscordOptions> options)
    {
        _discord = discord;
        _ingest = options.Value.Ingest;
    }

    [HttpGet("discord/users/{id}")]
    public async Task<ActionResult<DiscordUserDto>> GetUser(string id, CancellationToken ct)
    {
        var user = await _discord.GetUserAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Public URL and certificate fingerprint of the ingest route, from <c>Discord:Ingest</c>
    /// (api.env). Null while unset: the settings panel then sends users to the administrator.
    /// </summary>
    [HttpGet("discord/ingest-config")]
    public ActionResult<DiscordIngestConfigDto> GetIngestConfig() => Ok(new DiscordIngestConfigDto
    {
        PublicUrl = NullIfBlank(_ingest.PublicUrl),
        CertificateSha256 = NullIfBlank(_ingest.CertificateSha256),
    });

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
