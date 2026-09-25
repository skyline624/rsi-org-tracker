using System.Net.Http;
using System.Text.Json;
using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Models;
using Collector.Options;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Services;

/// <summary>
/// Interface for member collection operations.
/// </summary>
public interface IMemberCollector
{
    /// <summary>
    /// Phase 3: Collects members for all organizations.
    /// </summary>
    Task<int> CollectAllMembersAsync(CancellationToken ct = default);

    /// <summary>
    /// Collects members for a specific organization.
    /// </summary>
    Task<int> CollectMembersForOrganizationAsync(string orgSid, CancellationToken ct = default);
}

/// <summary>
/// Collects member data from the RSI API.
/// </summary>
public class MemberCollector : IMemberCollector
{
    private readonly IRsiApiClient _apiClient;
    private readonly IOrganizationRepository _orgRepo;
    private readonly IOrganizationMemberRepository _memberRepo;
    private readonly IMemberCollectionLogRepository _logRepo;
    private readonly IChangeEventRepository _changeEventRepo;
    private readonly IUserEnrichmentQueueRepository _enrichmentQueueRepo;
    private readonly IDiscoveredOrganizationRepository _discoveredRepo;
    private readonly IChangeDetector _changeDetector;
    private readonly IUserRepository _userRepo;
    private readonly ILogger<MemberCollector> _logger;
    private readonly CollectorOptions _options;

    public MemberCollector(
        IRsiApiClient apiClient,
        IOrganizationRepository orgRepo,
        IOrganizationMemberRepository memberRepo,
        IMemberCollectionLogRepository logRepo,
        IChangeEventRepository changeEventRepo,
        IUserEnrichmentQueueRepository enrichmentQueueRepo,
        IDiscoveredOrganizationRepository discoveredRepo,
        IChangeDetector changeDetector,
        IUserRepository userRepo,
        ILogger<MemberCollector> logger,
        IOptions<CollectorOptions> options)
    {
        _apiClient = apiClient;
        _orgRepo = orgRepo;
        _memberRepo = memberRepo;
        _logRepo = logRepo;
        _changeEventRepo = changeEventRepo;
        _enrichmentQueueRepo = enrichmentQueueRepo;
        _discoveredRepo = discoveredRepo;
        _changeDetector = changeDetector;
        _userRepo = userRepo;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> CollectAllMembersAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting member collection (Phase 3)");

        // Live discovered orgs, least recently collected first: a restart resumes where
        // the previous pass stopped instead of starting over from the same orgs.
        var sids = await _discoveredRepo.GetMemberCollectionTargetsAsync(ct);
        var totalMembers = 0;

        foreach (var sid in sids)
        {
            try
            {
                var count = await CollectMembersForOrganizationAsync(sid, ct);
                totalMembers += count;
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error collecting members for organization {Sid}", sid);
                // Continue with next organization
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error collecting members for organization {Sid}", sid);
                // Continue with next organization rather than aborting the entire Phase 3
            }

            // Also after a failure: a failing org goes to the back of the queue.
            await _discoveredRepo.MarkMembersCollectedAsync(sid, DateTime.UtcNow, ct);
        }

        _logger.LogInformation("Member collection complete: {Count} total members", totalMembers);
        return totalMembers;
    }

    public async Task<int> CollectMembersForOrganizationAsync(string orgSid, CancellationToken ct = default)
    {
        try
        {
            return await CollectOrganizationAsync(orgSid, ct);
        }
        finally
        {
            // One DbContext serves the whole phase: drop what this organization inserted
            // so memory stays flat instead of growing with every organization collected.
            _memberRepo.ClearTrackedEntities();
        }
    }

    private async Task<int> CollectOrganizationAsync(string orgSid, CancellationToken ct)
    {
        _logger.LogInformation("Collecting members for organization {Sid}", orgSid);

        // ── STEP 1 — fetch all inputs (reads only, no transaction) ──
        var collection = await _apiClient.GetAllOrganizationMembersAsync(
            orgSid, _options.MemberCollectionPageSize, ct);

        switch (collection.Status)
        {
            // Nothing read: we have no authoritative signal to act on.
            case RosterStatus.Unreachable:
                _logger.LogWarning("Members unreachable for {Sid}; keeping prior roster", orgSid);
                return 0;

            // ErrInvalidOrganization is the only answer that empties a roster: flush
            // it so the detail page no longer shows ghost members, and zero the count.
            case RosterStatus.OrgGone:
            {
                var deactivated = await _memberRepo.MarkAllPreviousInactiveAsync(orgSid, DateTime.UtcNow, ct);
                _logger.LogWarning(
                    "Organization {Sid} no longer exists — deactivated {Count} prior active rows",
                    orgSid, deactivated);
                await ReconcileMembersCountAsync(orgSid, 0, ct);
                return 0;
            }

            // A truncated roster would turn every unread member into a false departure.
            case RosterStatus.Partial:
            case RosterStatus.Capped:
                await ReconcileMembersCountAsync(orgSid, collection.TotalRows, ct);
                _logger.LogWarning(
                    "Roster of {Sid} not written ({Status}: {Raw}/{Total} rows read); keeping prior roster",
                    orgSid, collection.Status, collection.RawRows, collection.TotalRows);
                return 0;
        }

        // Every row read but none visible (all redacted or hidden): the members are
        // still there, so this is no reason to deactivate anyone.
        if (collection.Members.Count == 0)
        {
            await ReconcileMembersCountAsync(orgSid, collection.TotalRows, ct);
            _logger.LogInformation(
                "All {Total} members of {Sid} are masked; keeping prior roster", collection.TotalRows, orgSid);
            return 0;
        }

        var members = collection.Members;

        var previousLog = await _logRepo.GetLatestAsync(orgSid, ct);
        var previousSnapshots = previousLog != null
            ? await GetPreviousSnapshotsAsync(orgSid, previousLog.CollectionTime, ct)
            : new List<MemberSnapshot>();

        // Look up display names BEFORE opening the transaction — this can touch ~400k
        // rows of the users table on SQLite and we don't want it holding a write lock.
        var memberHandles = members.Select(m => m.Handle).ToList();
        var knownByHandle = await _userRepo.GetDisplayNamesByHandlesAsync(memberHandles, ct);

        // Skip handles already pending in the enrichment queue. Without this,
        // the orphan-rescue branch below would re-INSERT-OR-IGNORE every
        // already-pending handle on every cycle (~169k SQL no-ops per cycle on
        // a deep queue). The partial unique index still guarantees correctness;
        // this is purely a perf win.
        var pendingSet = new HashSet<string>(
            await _enrichmentQueueRepo.GetPendingHandlesInAsync(memberHandles, ct),
            StringComparer.OrdinalIgnoreCase);

        var timestamp = DateTime.UtcNow;

        var currentSnapshots = members.Select(m => new MemberSnapshot
        {
            CitizenId = m.CitizenId,
            Handle = m.Handle,
            Rank = m.Rank,
            Roles = m.Roles
        }).ToList();

        var previousHandleSet = new HashSet<string>(
            previousSnapshots.Select(s => s.Handle),
            StringComparer.OrdinalIgnoreCase);
        var newHandleSet = new HashSet<string>(
            members.Where(m => !previousHandleSet.Contains(m.Handle)).Select(m => m.Handle),
            StringComparer.OrdinalIgnoreCase);

        // Detect changes but suppress member_joined for new handles — Phase 4 will emit
        // them after verifying citizen_id (new player vs. renamed player).
        var allChanges = _changeDetector.DetectMemberChanges(orgSid, previousSnapshots, currentSnapshots);
        var changes = allChanges
            .Where(e => !(e.ChangeType == "member_joined" && e.UserHandle != null && newHandleSet.Contains(e.UserHandle)))
            .ToList();

        // Build the queue batch up-front; InsertPendingIgnoreDuplicatesAsync will
        // atomically drop anything that already has a pending row.
        // knownByHandle is keyed only by handles that actually exist in the users
        // table — absence from this dict means the member has never been enriched.
        var toQueue = new List<UserEnrichmentQueue>();
        foreach (var member in members)
        {
            // Already pending — Phase4Worker will pick it up; skip the redundant insert.
            if (pendingSet.Contains(member.Handle)) continue;

            if (newHandleSet.Contains(member.Handle))
            {
                toQueue.Add(new UserEnrichmentQueue
                {
                    UserHandle = member.Handle,
                    Priority = 1,
                    Enriched = false,
                    QueuedAt = timestamp
                });
            }
            else if (knownByHandle.TryGetValue(member.Handle, out var knownDisplayName))
            {
                if (knownDisplayName != member.DisplayName)
                {
                    toQueue.Add(new UserEnrichmentQueue
                    {
                        UserHandle = member.Handle,
                        Priority = 0,
                        Enriched = false,
                        QueuedAt = timestamp
                    });
                }
            }
            else
            {
                // Orphan: seen before as a member but never successfully enriched.
                // Re-queue at low priority so Phase 4 eventually catches up.
                toQueue.Add(new UserEnrichmentQueue
                {
                    UserHandle = member.Handle,
                    Priority = 0,
                    Enriched = false,
                    QueuedAt = timestamp
                });
            }
        }

        // ── STEP 2 — atomic write of member snapshots, logs, change events,
        //             AND the deactivation of the previous snapshot. All or nothing. ──
        await using (var transaction = await _memberRepo.BeginTransactionAsync(ct))
        {
            try
            {
                var memberEntities = members.Select(m => new OrganizationMember
                {
                    OrgSid = orgSid,
                    UserHandle = m.Handle,
                    CitizenId = m.CitizenId,
                    DisplayName = m.DisplayName,
                    Rank = m.Rank,
                    RolesJson = m.Roles != null ? JsonSerializer.Serialize(m.Roles) : null,
                    UrlImage = m.UrlImage,
                    Timestamp = timestamp
                }).ToList();

                await _memberRepo.AddRangeAsync(memberEntities, ct);

                var logEntries = members.Select(m => new MemberCollectionLog
                {
                    OrgSid = orgSid,
                    CollectionTime = timestamp,
                    CitizenId = m.CitizenId,
                    UserHandle = m.Handle,
                    Rank = m.Rank,
                    RolesJson = m.Roles != null ? JsonSerializer.Serialize(m.Roles) : null
                }).ToList();

                await _logRepo.AddRangeAsync(logEntries, ct);

                if (changes.Count > 0)
                {
                    await _changeEventRepo.AddRangeAsync(changes, ct);
                }

                await _memberRepo.SaveChangesAsync(ct);

                // Previously this ran OUTSIDE the transaction — a crash in between the
                // commit and the mark step would leave rows incorrectly active. Now it
                // is part of the same atomic unit.
                await _memberRepo.MarkAllPreviousInactiveAsync(orgSid, timestamp, ct);

                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                _memberRepo.ClearTrackedEntities();
                throw;
            }
        }

        // ── STEP 3 — best-effort queue insert OUTSIDE the transaction. Duplicates
        //             against the partial unique index are silently ignored by
        //             INSERT OR IGNORE, so a duplicate never tears down Step 2. ──
        var queued = 0;
        if (toQueue.Count > 0)
        {
            try
            {
                queued = await _enrichmentQueueRepo.InsertPendingIgnoreDuplicatesAsync(toQueue, ct);
            }
            catch (Exception ex)
            {
                // Logging only — the member snapshot is the source of truth, the
                // queue can always be rebuilt on the next cycle.
                _logger.LogWarning(ex,
                    "Queue insert failed for org {Sid} — {Count} handles skipped", orgSid, toQueue.Count);
            }
        }

        // ── STEP 4 — reconcile Organization.MembersCount with RSI's row count.
        await ReconcileMembersCountAsync(orgSid, collection.TotalRows, ct);

        _logger.LogInformation(
            "Collected {Count} members for {Sid}, detected {Changes} changes, queued {NewUsers} new users",
            members.Count, orgSid, changes.Count, queued);

        return members.Count;
    }

    /// <summary>
    /// Sets the latest organization snapshot's MembersCount to RSI's totalrows, which
    /// counts redacted and hidden members too (the visible roster does not). Phase 1's
    /// search listing carries the same counter but a cached, sometimes older value.
    /// </summary>
    private async Task ReconcileMembersCountAsync(string orgSid, int totalRows, CancellationToken ct)
    {
        try
        {
            var updated = await _orgRepo.UpdateLatestMembersCountAsync(orgSid, totalRows, ct);
            if (updated > 0)
            {
                _logger.LogInformation("Reconciled MembersCount for {Sid}: {Count}", orgSid, totalRows);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MembersCount reconciliation failed for {Sid} (non-fatal)", orgSid);
        }
    }

    private async Task<IReadOnlyList<MemberSnapshot>> GetPreviousSnapshotsAsync(
        string orgSid,
        DateTime collectionTime,
        CancellationToken ct)
    {
        var logs = await _logRepo.GetByCollectionTimeAsync(orgSid, collectionTime, ct);
        return _changeDetector.CreateSnapshotsFromLogs(logs);
    }
}