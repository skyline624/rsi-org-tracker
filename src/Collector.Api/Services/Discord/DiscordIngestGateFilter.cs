using Collector.Api.Errors;
using Collector.Data.Repositories;
using Collector.Discord;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Resource filter of the ingest action (spec § 9.1 step 1). It runs after authentication,
/// the rate limiter and authorization, and before the body is bound, so an invalid or excluded
/// guild is refused without reading up to 25 MiB. It holds the Discord write gate from model
/// binding to the end of the response, and releases it whatever happens.
/// </summary>
public sealed class DiscordIngestGateFilter : IAsyncResourceFilter
{
    /// <summary>Key of the lease in <c>HttpContext.Items</c> while the action runs.</summary>
    public const string LeaseItemKey = "DiscordWriteGate";
    public const string ReceivedAtItemKey = "DiscordReceivedAt";

    /// <summary>Seconds a refused writer is told to wait (<c>Retry-After</c>).</summary>
    public const int RetryAfterSeconds = 30;

    private readonly DiscordWriteGate _gate;
    private readonly IDiscordRosterRepository _roster;
    private readonly TimeProvider _time;

    public DiscordIngestGateFilter(DiscordWriteGate gate, IDiscordRosterRepository roster, TimeProvider? time = null)
    {
        _gate = gate;
        _roster = roster;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long a request waits for the gate before a 503. Unit tests shorten it.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        // Capture before exclusion reads and the gate wait: waiting must never make an older collection newer.
        context.HttpContext.Items[ReceivedAtItemKey] = _time.GetUtcNow().UtcDateTime;
        var guildId = context.RouteData.Values["guildId"] as string;
        if (!DiscordSnowflake.IsValid(guildId))
            throw new ValidationException("guildId (URL) : snowflake attendu.", DiscordErrorCodes.InvalidSync);

        var ct = context.HttpContext.RequestAborted;
        if (await _roster.IsGuildExcludedAsync(guildId!, ct)) throw GuildExcluded();

        var lease = await _gate.TryEnterAsync(WaitTimeout, ct)
            ?? throw new ServiceUnavailableException(
                "Tracker occupé, réessaie dans 30 s.", RetryAfterSeconds, DiscordErrorCodes.Busy);
        try
        {
            // An admin may have excluded the guild while this request waited. Erasures take
            // the same gate, so this second check cannot be overtaken.
            if (await _roster.IsGuildExcludedAsync(guildId!, ct)) throw GuildExcluded();

            context.HttpContext.Items[LeaseItemKey] = lease;
            await next();
        }
        finally
        {
            context.HttpContext.Items.Remove(LeaseItemKey);
            lease.Dispose();
        }
    }

    private static ConflictException GuildExcluded() =>
        new("Ce serveur est exclu du suivi.", DiscordErrorCodes.GuildExcluded);
}
