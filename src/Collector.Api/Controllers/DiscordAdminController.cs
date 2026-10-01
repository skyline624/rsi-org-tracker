using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Api.Services;
using Collector.Api.Services.Discord;
using Collector.Data.Repositories;
using Collector.Discord;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Collector.Api.Controllers;

/// <summary>
/// Admin-only erasure and opposition for the Discord rosters (spec § 13.2), accessible outside the web interface. Every write holds the Discord write gate so it
/// never interleaves with an ingestion, and is recorded in activity_logs once committed.
/// An admin calls these routes directly; the web interface exposes no erasure commands.
/// </summary>
[ApiController]
[Route("api/discord")]
[Authorize(Policy = "AdminOnly")]
public class DiscordAdminController : ControllerBase
{
    private const int RetryAfterSeconds = 30;
    private const string AccountEntity = "discord_account";
    private const string GuildEntity = "discord_guild";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private readonly IDiscordErasureRepository _erasure;
    private readonly DiscordWriteGate _gate;
    private readonly ActivityLogService _activityLog;
    private readonly CurrentUserAccessor _currentUser;
    private readonly ILogger<DiscordAdminController> _logger;

    public DiscordAdminController(
        IDiscordErasureRepository erasure,
        DiscordWriteGate gate,
        ActivityLogService activityLog,
        CurrentUserAccessor currentUser,
        ILogger<DiscordAdminController> logger)
    {
        _erasure = erasure;
        _gate = gate;
        _activityLog = activityLog;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>Deletes everything stored about a Discord account and keeps it out of later syncs.</summary>
    [HttpDelete("accounts/{discordUserId}")]
    public async Task<IActionResult> EraseAccount(string discordUserId, CancellationToken ct)
    {
        RequireSnowflake(discordUserId, "discordUserId");
        await UnderGateAsync(async c =>
        {
            await _erasure.EraseAccountAsync(discordUserId, ActorId, ActorName, c);
            return true;
        }, ct);
        await AuditAsync(DiscordAudit.EraseAccount, AccountEntity, discordUserId);
        return NoContent();
    }

    /// <summary>
    /// Deletes a guild's roster and history. <c>exclude=true</c> also refuses its later syncs
    /// (409 guild_excluded). <c>exclude=false</c> makes the next sync a new baseline.
    /// </summary>
    [HttpDelete("guilds/{guildId}")]
    public async Task<IActionResult> EraseGuild(string guildId, [FromQuery, BindRequired] bool exclude, CancellationToken ct)
    {
        RequireSnowflake(guildId, "guildId");
        var erased = await UnderGateAsync(c => _erasure.EraseGuildAsync(guildId, exclude, ActorId, ActorName, c), ct);
        if (!erased) return NotFound();
        await AuditAsync(DiscordAudit.EraseGuild, GuildEntity, guildId);
        return NoContent();
    }

    [HttpGet("optouts")]
    public async Task<ActionResult<IReadOnlyList<DiscordOptOutDto>>> ListOptOuts(CancellationToken ct)
    {
        var rows = await _erasure.ListOptOutsAsync(ct);
        return Ok(rows.Select(o => new DiscordOptOutDto
        {
            DiscordUserId = o.DiscordUserId,
            CreatedAt = o.CreatedAt,
            ByUsername = o.ByUsername,
            Reason = o.Reason,
        }).ToList());
    }

    /// <summary>Lets later syncs record this account again.</summary>
    [HttpDelete("optouts/{discordUserId}")]
    public async Task<IActionResult> RemoveOptOut(string discordUserId, CancellationToken ct)
    {
        RequireSnowflake(discordUserId, "discordUserId");
        var removed = await UnderGateAsync(c => _erasure.RemoveOptOutAsync(discordUserId, c), ct);
        if (!removed) return NotFound();
        await AuditAsync(DiscordAudit.RemoveOptOut, AccountEntity, discordUserId);
        return NoContent();
    }

    [HttpGet("guild-optouts")]
    public async Task<ActionResult<IReadOnlyList<DiscordGuildOptOutDto>>> ListGuildOptOuts(CancellationToken ct)
    {
        var rows = await _erasure.ListGuildOptOutsAsync(ct);
        return Ok(rows.Select(o => new DiscordGuildOptOutDto
        {
            GuildId = o.GuildId,
            CreatedAt = o.CreatedAt,
            ByUsername = o.ByUsername,
            Reason = o.Reason,
        }).ToList());
    }

    /// <summary>Accepts syncs of this guild again; the next one is a baseline.</summary>
    [HttpDelete("guild-optouts/{guildId}")]
    public async Task<IActionResult> RemoveGuildOptOut(string guildId, CancellationToken ct)
    {
        RequireSnowflake(guildId, "guildId");
        var removed = await UnderGateAsync(c => _erasure.RemoveGuildOptOutAsync(guildId, c), ct);
        if (!removed) return NotFound();
        await AuditAsync(DiscordAudit.RemoveGuildOptOut, GuildEntity, guildId);
        return NoContent();
    }

    /// <summary>The static admin key (user id 0) has no api_users row.</summary>
    private long? ActorId => _currentUser.UserId > 0 ? _currentUser.UserId : null;

    private string ActorName => _currentUser.Username ?? "admin";

    private static void RequireSnowflake(string value, string name)
    {
        if (!DiscordSnowflake.IsValid(value)) throw new ValidationException($"{name} : snowflake attendu.");
    }

    /// <summary>Runs one write under the Discord write gate: 503 when the gate or tracker.db stays busy.</summary>
    private async Task<T> UnderGateAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken ct)
    {
        using var lease = await _gate.TryEnterAsync(GateTimeout, ct)
            ?? throw new ServiceUnavailableException("Tracker occupé, réessaie dans 30 s.", RetryAfterSeconds);
        try
        {
            return await write(ct);
        }
        catch (DiscordStoreBusyException)
        {
            throw new ServiceUnavailableException("Base du tracker occupée, réessaie dans 30 s.", RetryAfterSeconds);
        }
    }

    // CancellationToken.None: the change is committed, so its audit entry must not depend on
    // the client staying connected.
    private Task AuditAsync(string action, string entityType, string entityId) =>
        DiscordAudit.LogAsync(_activityLog, _currentUser, _logger, action, entityType, entityId, CancellationToken.None);
}
