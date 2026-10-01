using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Turns a plugin sync into roster rows and events (spec § 9.1 steps 2–6). It runs while the
/// request holds the Discord write gate (<see cref="DiscordIngestGateFilter"/>), so no other
/// Discord writer can slip between the stale check, the snapshot, the diff and the writes.
/// </summary>
public sealed class DiscordIngestService
{
    /// <summary>Seconds a writer is told to wait when tracker.db stays busy.</summary>
    public const int BusyRetryAfterSeconds = 30;

    private readonly IDiscordRosterRepository _roster;
    private readonly ILogger<DiscordIngestService> _logger;

    public DiscordIngestService(IDiscordRosterRepository roster, ILogger<DiscordIngestService> logger)
    {
        _roster = roster;
        _logger = logger;
    }

    public async Task<DiscordSyncResponseDto> IngestAsync(
        string guildId, DiscordSyncRequest request, DateTime receivedAt,
        long submitterId, string submitterName, CancellationToken ct)
    {
        var sync = DiscordSyncValidator.Normalize(guildId, request);

        // The plugin's clock is only informative. The sync is dated from the server's reception
        // minus the measured collection time, which is when the collection started.
        var collectedAt = receivedAt - sync.CollectionDuration;

        // A sync accepted after this collection started may already hold newer data.
        var lastReceivedAt = await _roster.GetLastSyncReceivedAtAsync(guildId, ct);
        if (lastReceivedAt > collectedAt)
            throw new ConflictException("Un envoi plus récent a été reçu pendant ta collecte.", DiscordErrorCodes.StaleSync);

        var userIds = sync.Members.Select(m => m.UserId).ToList();
        var snapshot = await _roster.LoadSnapshotAsync(guildId, userIds, ct);
        // An interrupted erasure can leave an opted-out membership absent from this payload.
        // Include stored members so neither departures nor their signal count an opted-out account.
        var optOutCandidates = userIds.Concat(snapshot.Members.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var optedOut = await _roster.GetOptedOutAsync(optOutCandidates, ct);
        var plan = DiscordRosterDiff.Compute(guildId, sync, collectedAt, snapshot, optedOut);

        DiscordSyncResult result;
        try
        {
            result = await _roster.ApplyAsync(
                new DiscordSyncWrite(sync, plan, collectedAt, receivedAt, submitterId, submitterName), ct);
        }
        catch (DiscordStoreBusyException ex)
        {
            // The transactions already committed stay valid; the next sync completes the rest.
            _logger.LogWarning(ex, "tracker.db stayed busy while storing a sync of guild {GuildId}", guildId);
            throw new ServiceUnavailableException(
                "Base du tracker occupée, réessaie dans 30 s.", BusyRetryAfterSeconds, DiscordErrorCodes.Busy);
        }

        return new DiscordSyncResponseDto
        {
            SyncId = result.SyncId,
            IsBaseline = plan.IsBaseline,
            IsComplete = plan.IsComplete,
            MassDepartureDetected = plan.MassDepartureDetected,
            OrgSid = result.OrgSid,
            MembersReceived = sync.CollectedCount,
            MembersOptedOut = plan.OptedOutCount,
            UnknownRoleRefs = sync.UnknownRoleRefCount,
            Events = CountEvents(plan.Events),
        };
    }

    private static DiscordSyncEventCountsDto CountEvents(IReadOnlyList<PlannedEvent> events)
    {
        var counts = new DiscordSyncEventCountsDto();
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case DiscordEventTypes.Joined: counts.Joined++; break;
                case DiscordEventTypes.Left: counts.Left++; break;
                case DiscordEventTypes.Rejoined: counts.Rejoined++; break;
                case DiscordEventTypes.RolesChanged: counts.RolesChanged++; break;
                case DiscordEventTypes.NickChanged: counts.NickChanged++; break;
                case DiscordEventTypes.UsernameChanged:
                case DiscordEventTypes.GlobalNameChanged: counts.NameChanged++; break;
            }
        }
        return counts;
    }
}
