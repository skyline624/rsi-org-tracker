using System.Net.Http;
using System.Text.Json;
using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Models;
using Collector.Options;
using Collector.Parsers;
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
    private readonly IOrgMemberCountRepository _countRepo;
    private readonly IChangeDetector _changeDetector;
    private readonly IUserRepository _userRepo;
    private readonly RosterCarryOver _carryOver;
    private readonly ILogger<MemberCollector> _logger;
    private readonly CollectorOptions _options;

    /// <summary>A handle that ended gone or abandoned is not queued again before this delay.</summary>
    private static readonly TimeSpan RequeueCooldown = TimeSpan.FromDays(30);

    public MemberCollector(
        IRsiApiClient apiClient,
        IOrganizationRepository orgRepo,
        IOrganizationMemberRepository memberRepo,
        IMemberCollectionLogRepository logRepo,
        IChangeEventRepository changeEventRepo,
        IUserEnrichmentQueueRepository enrichmentQueueRepo,
        IDiscoveredOrganizationRepository discoveredRepo,
        IOrgMemberCountRepository countRepo,
        IChangeDetector changeDetector,
        IUserRepository userRepo,
        RosterCarryOver carryOver,
        ILogger<MemberCollector> logger,
        IOptions<CollectorOptions> options)
    {
        _carryOver = carryOver;
        _apiClient = apiClient;
        _orgRepo = orgRepo;
        _memberRepo = memberRepo;
        _logRepo = logRepo;
        _changeEventRepo = changeEventRepo;
        _enrichmentQueueRepo = enrichmentQueueRepo;
        _discoveredRepo = discoveredRepo;
        _countRepo = countRepo;
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

        await RecordCountersAsync(orgSid, collection, ct);

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
                if (collection.TotalRows > 0)
                {
                    await ReconcileMembersCountAsync(orgSid, collection.TotalRows, ct);
                }
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

        IReadOnlySet<string> carried = new HashSet<string>();
        IReadOnlyList<MemberData> members = collection.Members;
        if (collection.Status == RosterStatus.Capped)
        {
            members = await WithMembersBeyondTheWindowAsync(orgSid, collection.Members, ct);
        }
        else if (collection.Status == RosterStatus.Short)
        {
            (members, carried) = await WithMembersSkippedByAShortReadAsync(orgSid, collection, ct);
        }

        var previousLog = await _logRepo.GetLatestAsync(orgSid, ct);
        var previousSnapshots = previousLog != null
            ? await GetPreviousSnapshotsAsync(orgSid, previousLog.CollectionTime, ct)
            : new List<MemberSnapshot>();

        // First collection of the org, or first since the v1 parser whose ranks were
        // overlay titles: this pass is the reference, comparing would only be noise.
        var baseline = previousLog == null || previousLog.ParserVersion < MemberHtmlParser.Version;

        // Roster rows carry no citizen number. Take it from users / handle history on
        // both sides, so a renamed member matches itself and a known citizen is recognized.
        var citizenIds = await _userRepo.GetCitizenIdsByHandlesAsync(
            members.Select(m => m.Handle).Concat(previousSnapshots.Select(s => s.Handle))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ct);
        int? CitizenIdOf(string handle, int? stored) => citizenIds.TryGetValue(handle, out var id) ? id : stored;
        foreach (var member in members)
        {
            member.CitizenId = CitizenIdOf(member.Handle, member.CitizenId);
        }
        previousSnapshots = previousSnapshots
            .Select(s => s with { CitizenId = CitizenIdOf(s.Handle, s.CitizenId) })
            .ToList();

        // Look up display names BEFORE opening the transaction — this can touch ~400k
        // rows of the users table on SQLite and we don't want it holding a write lock.
        var memberHandles = members.Select(m => m.Handle).ToList();

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

        // A known citizen new to the org is announced now. An unknown handle waits for
        // Phase 4, which reads its profile and tells a new player from a renamed one.
        var allChanges = _changeDetector.DetectMemberChanges(orgSid, previousSnapshots, currentSnapshots);
        var changes = baseline
            ? []
            : allChanges
                .Where(e => !(e.ChangeType == "member_joined" && e.UserHandle != null
                    && newHandleSet.Contains(e.UserHandle) && !citizenIds.ContainsKey(e.UserHandle)))
                .ToList();
        if (baseline && previousLog != null)
        {
            _logger.LogInformation(
                "First collection of {Sid} with roster parser v{Version}: reference pass, no member events",
                orgSid, MemberHtmlParser.Version);
        }

        // Build the queue batch up-front; InsertPendingIgnoreDuplicatesAsync will
        // atomically drop anything that already has a pending row.
        // knownByHandle is keyed only by handles that actually exist in the users
        // table — absence from this dict means the member has never been enriched.
        // Only members whose citizen is unknown go to Phase 4: a newcomer first
        // (priority 1, Phase 4 tells a new player from a renamed one), else an orphan
        // seen before but never identified (priority 0). A known citizen is never
        // re-read for a display name difference (roster and profile names differ by
        // nature, which used to re-queue most members every cycle), and a handle that
        // recently ended gone or abandoned is left alone.
        var unknown = members.Where(m => !citizenIds.ContainsKey(m.Handle) && !pendingSet.Contains(m.Handle)).ToList();
        var recentlySettled = (await _enrichmentQueueRepo.GetRecentlySettledHandlesInAsync(
                unknown.Select(m => m.Handle).ToList(), timestamp - RequeueCooldown, ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toQueue = unknown
            .Where(m => !recentlySettled.Contains(m.Handle))
            .Select(m => new UserEnrichmentQueue
            {
                UserHandle = m.Handle,
                Priority = newHandleSet.Contains(m.Handle) ? 1 : 0,
                Enriched = false,
                QueuedAt = timestamp
            })
            .ToList();

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
                    Stars = m.Stars,
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
                    RolesJson = m.Roles != null ? JsonSerializer.Serialize(m.Roles) : null,
                    ParserVersion = MemberHtmlParser.Version
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

        // Only once the roster is written, so a failed write does not hasten a departure.
        if (collection.Status is RosterStatus.Complete or RosterStatus.Short)
        {
            _carryOver.Record(orgSid, carried);
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
    /// Stores the roster counters when they changed. Only a complete read gives the
    /// visible / redacted / hidden split; otherwise the total alone is kept.
    /// </summary>
    private async Task RecordCountersAsync(string orgSid, MemberCollectionResult roster, CancellationToken ct)
    {
        // RSI answering 0 rows is a glitch, not a count: only ErrInvalidOrganization empties an org.
        if (roster.Status is RosterStatus.Unreachable or RosterStatus.OrgGone || roster.TotalRows <= 0) return;

        var complete = roster.Status == RosterStatus.Complete;
        var now = DateTime.UtcNow;
        try
        {
            var (written, previous) = await _countRepo.RecordIfChangedAsync(new OrgMemberCount
            {
                OrgSid = orgSid,
                CollectedAt = now,
                TotalRows = roster.TotalRows,
                VisibleCount = complete ? roster.RawRows - roster.RedactedRows - roster.HiddenRows : null,
                RedactedCount = complete ? roster.RedactedRows : null,
                HiddenCount = complete ? roster.HiddenRows : null,
            }, ct);

            // RSI's totalrows is the member count; Phase 1's listing only has a cached copy.
            if (written && previous != null && previous.TotalRows != roster.TotalRows)
            {
                await _changeEventRepo.AddRangeAsync([new ChangeEvent
                {
                    Timestamp = now,
                    EntityType = "organization",
                    EntityId = orgSid,
                    ChangeType = "member_count_changed",
                    OldValue = previous.TotalRows.ToString(),
                    NewValue = roster.TotalRows.ToString(),
                    OrgSid = orgSid,
                }], ct);
                await _changeEventRepo.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record member counters for {Sid} (non-fatal)", orgSid);
        }
    }

    /// <summary>
    /// RSI serves only the first 400 roster pages. For a larger org, members active in
    /// our last snapshot but outside that window are kept with their last known values:
    /// from the window alone, a departure cannot be told from a member out of sight.
    /// </summary>
    private async Task<IReadOnlyList<MemberData>> WithMembersBeyondTheWindowAsync(
        string orgSid, IReadOnlyList<MemberData> window, CancellationToken ct)
    {
        var beyond = await ActiveMembersNotReadAsync(orgSid, window, ct);

        _logger.LogInformation(
            "Roster of {Sid} exceeds RSI's {Pages}-page window: {Window} members read, {Beyond} kept from the last snapshot",
            orgSid, RsiApiClient.MaxRosterPages, window.Count, beyond.Count);
        return [.. window, .. beyond];
    }

    /// <summary>
    /// A short read (see <see cref="RosterStatus.Short"/>) may have skipped a row where
    /// the roster shifted: a member it missed is kept with its last known values once;
    /// missed by the next short read too, it is left out and counts as a departure.
    /// </summary>
    private async Task<(IReadOnlyList<MemberData> Members, IReadOnlySet<string> Carried)> WithMembersSkippedByAShortReadAsync(
        string orgSid, MemberCollectionResult roster, CancellationToken ct)
    {
        var missing = await ActiveMembersNotReadAsync(orgSid, roster.Members, ct);
        var carried = _carryOver.ToCarry(orgSid, missing.Select(m => m.Handle));

        _logger.LogInformation(
            "Short roster read of {Sid} ({Read} of {Total} rows): {Carried} missing members carried over, {Left} missing twice",
            orgSid, roster.Members.Count + roster.RedactedRows + roster.HiddenRows, roster.TotalRows,
            carried.Count, missing.Count - carried.Count);
        return ([.. roster.Members, .. missing.Where(m => carried.Contains(m.Handle))], carried);
    }

    /// <summary>Active members of our last snapshot that the read did not see, with their last known values.</summary>
    private async Task<List<MemberData>> ActiveMembersNotReadAsync(
        string orgSid, IReadOnlyList<MemberData> read, CancellationToken ct)
    {
        var seen = read.Select(m => m.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (await _memberRepo.GetActiveByOrgSidAsync(orgSid, ct))
            .Where(m => !seen.Contains(m.UserHandle))
            .Select(m => new MemberData
            {
                OrgSid = orgSid,
                Handle = m.UserHandle,
                CitizenId = m.CitizenId,
                DisplayName = m.DisplayName,
                Rank = m.Rank,
                Stars = m.Stars,
                Roles = m.RolesJson != null ? JsonSerializer.Deserialize<string[]>(m.RolesJson) : null,
                UrlImage = m.UrlImage,
            })
            .ToList();
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