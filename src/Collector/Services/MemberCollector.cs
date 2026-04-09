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
        _changeDetector = changeDetector;
        _userRepo = userRepo;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> CollectAllMembersAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting member collection (Phase 3)");

        var organizations = await _orgRepo.GetAllLatestAsync(ct);
        var totalMembers = 0;

        foreach (var org in organizations)
        {
            try
            {
                var count = await CollectMembersForOrganizationAsync(org.Sid, ct);
                totalMembers += count;
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error collecting members for organization {Sid}", org.Sid);
                // Continue with next organization
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error collecting members for organization {Sid}", org.Sid);
                // Continue with next organization rather than aborting the entire Phase 3
            }
        }

        _logger.LogInformation("Member collection complete: {Count} total members", totalMembers);
        return totalMembers;
    }

    public async Task<int> CollectMembersForOrganizationAsync(string orgSid, CancellationToken ct = default)
    {
        _logger.LogInformation("Collecting members for organization {Sid}", orgSid);

        // ── STEP 1 — fetch all inputs (reads only, no transaction) ──
        var collection = await _apiClient.GetAllOrganizationMembersAsync(
            orgSid, _options.MemberCollectionPageSize, ct);

        // If the fetch failed entirely (network/5xx/parse), leave the existing
        // roster alone — we have no authoritative signal to act on.
        if (!collection.Reachable)
        {
            _logger.LogWarning("Members unreachable for {Sid}; keeping prior roster", orgSid);
            return 0;
        }

        // RSI signalled the org no longer exists, OR the org genuinely has zero
        // members now. In either case the previously-active roster is stale —
        // flush it so the detail page no longer shows ghost members.
        if (!collection.OrgExists || collection.Members.Count == 0)
        {
            var deactivated = await _memberRepo.MarkAllPreviousInactiveAsync(
                orgSid, DateTime.UtcNow, ct);
            _logger.LogWarning(
                "No members for {Sid} (exists={Exists}) — deactivated {Count} prior active rows",
                orgSid, collection.OrgExists, deactivated);
            // Also zero the latest Organization snapshot so the list page stops
            // showing a stale headcount for a dead org.
            try
            {
                await _orgRepo.UpdateLatestMembersCountAsync(orgSid, 0, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not zero MembersCount for {Sid}", orgSid);
            }
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
        var toQueue = new List<UserEnrichmentQueue>();
        foreach (var member in members)
        {
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
            else if (knownByHandle.TryGetValue(member.Handle, out var knownDisplayName)
                     && knownDisplayName != member.DisplayName)
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

        // ── STEP 4 — reconcile Organization.MembersCount with the real headcount.
        //             Phase 1 reads the count from RSI's search API which occasionally
        //             reports 0 for active orgs. Phase 3 just counted the real roster,
        //             so we overwrite the latest Organization snapshot if it diverges.
        try
        {
            var realCount = members.Select(m => m.Handle)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var updated = await _orgRepo.UpdateLatestMembersCountAsync(orgSid, realCount, ct);
            if (updated > 0)
            {
                _logger.LogInformation(
                    "Reconciled MembersCount for {Sid}: Phase1=stale → Phase3={Count}",
                    orgSid, realCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MembersCount reconciliation failed for {Sid} (non-fatal)", orgSid);
        }

        _logger.LogInformation(
            "Collected {Count} members for {Sid}, detected {Changes} changes, queued {NewUsers} new users",
            members.Count, orgSid, changes.Count, queued);

        return members.Count;
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