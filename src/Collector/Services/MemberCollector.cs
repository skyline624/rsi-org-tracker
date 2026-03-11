using System.Text.Json;
using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Models;
using Collector.Options;
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting members for organization {Sid}", org.Sid);
            }
        }

        _logger.LogInformation("Member collection complete: {Count} total members", totalMembers);
        return totalMembers;
    }

    public async Task<int> CollectMembersForOrganizationAsync(string orgSid, CancellationToken ct = default)
    {
        _logger.LogInformation("Collecting members for organization {Sid}", orgSid);

        // Fetch all members from API
        var members = await _apiClient.GetAllOrganizationMembersAsync(
            orgSid, _options.MemberCollectionPageSize, ct);

        if (members.Count == 0)
        {
            _logger.LogWarning("No members found for organization {Sid}", orgSid);
            return 0;
        }

        // Get previous collection log for change detection
        var previousLog = await _logRepo.GetLatestAsync(orgSid, ct);
        var previousSnapshots = previousLog != null
            ? await GetPreviousSnapshotsAsync(orgSid, previousLog.CollectionTime, ct)
            : new List<MemberSnapshot>();

        var timestamp = DateTime.UtcNow;

        // Create current snapshots
        var currentSnapshots = members.Select(m => new MemberSnapshot
        {
            CitizenId = m.CitizenId,
            Handle = m.Handle,
            Rank = m.Rank,
            Roles = m.Roles
        }).ToList();

        // Detect changes
        var changes = _changeDetector.DetectMemberChanges(orgSid, previousSnapshots, currentSnapshots);

        // Save member snapshots
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

        // Save collection log
        var logEntries = members.Select(m => new MemberCollectionLog
        {
            OrgSid = orgSid,
            CollectionTime = timestamp,
            CitizenId = m.CitizenId,
            UserHandle = m.Handle
        }).ToList();

        await _logRepo.AddRangeAsync(logEntries, ct);

        // Save change events
        if (changes.Count > 0)
        {
            await _changeEventRepo.AddRangeAsync(changes, ct);
        }

        // Queue new users for enrichment
        var knownHandles = await _userRepo.GetAllAsync(ct);
        var knownHandleSet = new HashSet<string>(knownHandles.Select(u => u.UserHandle), StringComparer.OrdinalIgnoreCase);

        var newMembers = members.Where(m => !knownHandleSet.Contains(m.Handle)).ToList();
        var queuedHandles = await _enrichmentQueueRepo.GetPendingAsync(_options.BatchSize, ct);
        var queuedHandleSet = new HashSet<string>(queuedHandles.Select(q => q.UserHandle), StringComparer.OrdinalIgnoreCase);

        var toQueue = newMembers
            .Where(m => !queuedHandleSet.Contains(m.Handle))
            .Select(m => new UserEnrichmentQueue
            {
                UserHandle = m.Handle,
                Priority = 0,
                Enriched = false,
                QueuedAt = timestamp
            })
            .ToList();

        if (toQueue.Count > 0)
        {
            await _enrichmentQueueRepo.AddRangeAsync(toQueue, ct);
        }

        await _memberRepo.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Collected {Count} members for {Sid}, detected {Changes} changes, queued {NewUsers} new users",
            members.Count, orgSid, changes.Count, toQueue.Count);

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