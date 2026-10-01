using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Collector.Api.Controllers;

/// <summary>
/// The only API route reachable from outside the VPS (nginx <c>/ingest/discord/</c>). The
/// Vencord plugin posts a guild roster here with a discord:ingest key. The gate filter
/// validates the guild id, refuses excluded guilds and holds the Discord write gate around
/// model binding and the action, so the body is read only once the request may write.
/// </summary>
[ApiController]
[Route("api/ingest/discord")]
[Authorize(Policy = DiscordIngestAuth.PolicyName)]
[EnableRateLimiting(RateLimitingExtensions.DiscordIngestPolicy)]
public class DiscordIngestController : ControllerBase
{
    /// <summary>25 MiB: about 50,000 members at worst-case field lengths (spec § 6.3).</summary>
    public const long MaxBodyBytes = 25L * 1024 * 1024;

    private readonly DiscordIngestService _ingest;
    private readonly CurrentUserAccessor _currentUser;
    private readonly TimeProvider _time;

    public DiscordIngestController(DiscordIngestService ingest, CurrentUserAccessor currentUser, TimeProvider time)
    {
        _ingest = ingest;
        _currentUser = currentUser;
        _time = time;
    }

    /// <summary>
    /// Stores one roster sync using its arrival before the gate wait for the stale-collection check.
    /// </summary>
    [HttpPost("guilds/{guildId}/syncs")]
    [RequestSizeLimit(MaxBodyBytes)]
    [TypeFilter(typeof(DiscordIngestGateFilter))]
    public async Task<ActionResult<DiscordSyncResponseDto>> PostSync(
        string guildId, [FromBody] DiscordSyncRequest request, CancellationToken ct)
    {
        var receivedAt = HttpContext.Items[DiscordIngestGateFilter.ReceivedAtItemKey] is DateTime arrival
            ? arrival : _time.GetUtcNow().UtcDateTime;
        var submitterId = _currentUser.UserId ?? throw new AuthenticationFailedException("Authentication required");
        var submitterName = _currentUser.Username ?? "unknown";
        return Ok(await _ingest.IngestAsync(guildId, request, receivedAt, submitterId, submitterName, ct));
    }
}
