using Collector.Api.Dtos.Discord;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Link suggestions, links and rejections (spec § 10.1). The route prefix ("api"), the
/// [Authorize] requirement and the base class come from the main part of the controller.
/// </summary>
public partial class DiscordRostersController
{
    /// <summary>Suggested links for the guild's active, non-bot, unlinked members.</summary>
    [HttpGet("discord/guilds/{guildId}/suggestions")]
    public async Task<ActionResult<IReadOnlyList<DiscordSuggestionDto>>> GetLinkSuggestions(
        string guildId, [FromServices] DiscordSuggestionService suggestions, CancellationToken ct)
        => Ok(await suggestions.GetSuggestionsAsync(guildId, ct));

    /// <summary>Validates a suggestion: links the Discord id to the person's entity.</summary>
    [HttpPost("discord/links")]
    public async Task<ActionResult<DiscordLinkCreatedDto>> CreateDiscordLink(
        [FromBody] CreateDiscordLinkRequest request, [FromServices] DiscordSuggestionService suggestions,
        CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await suggestions.LinkAsync(request, ct));

    /// <summary>Ignores a suggestion.</summary>
    [HttpPost("discord/link-rejections")]
    public async Task<ActionResult<DiscordLinkRejectionCreatedDto>> CreateLinkRejection(
        [FromBody] CreateDiscordLinkRejectionRequest request, [FromServices] DiscordSuggestionService suggestions,
        CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created,
            new DiscordLinkRejectionCreatedDto { Id = await suggestions.RejectAsync(request, ct) });

    /// <summary>Undoes a rejection (its author or an admin).</summary>
    [HttpDelete("discord/link-rejections/{id:long}")]
    public async Task<IActionResult> DeleteLinkRejection(
        long id, [FromServices] DiscordSuggestionService suggestions, CancellationToken ct)
    {
        await suggestions.DeleteRejectionAsync(id, ct);
        return NoContent();
    }
}
