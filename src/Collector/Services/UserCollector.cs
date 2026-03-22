using System.Net.Http;
using System.Text.Json;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Options;
using Collector.Parsers;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Services;

/// <summary>
/// Interface for user enrichment operations.
/// </summary>
public interface IUserCollector
{
    /// <summary>
    /// Phase 4: Enriches user profiles from RSI citizen pages.
    /// </summary>
    Task<int> EnrichAllUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Enriches a single user profile from pre-fetched HTML.
    /// </summary>
    /// <param name="isNewHandle">True if this handle was never seen before — Phase 4 will emit member_joined if truly new, or handle_changed if renamed.</param>
    /// <param name="html">Pre-fetched profile page HTML.</param>
    Task<bool> EnrichUserAsync(string handle, bool isNewHandle, string html, CancellationToken ct = default);
}

/// <summary>
/// Collects user profile data from RSI citizen pages.
/// </summary>
public class UserCollector : IUserCollector
{
    private readonly IRsiApiClient _apiClient;
    private readonly IUserRepository _userRepo;
    private readonly IUserHandleHistoryRepository _handleHistoryRepo;
    private readonly IUserEnrichmentQueueRepository _queueRepo;
    private readonly IOrganizationMemberRepository _memberRepo;
    private readonly IChangeEventRepository _changeEventRepo;
    private readonly IUserChangeDetector _userChangeDetector;
    private readonly UserProfileHtmlParser _profileParser;
    private readonly ILogger<UserCollector> _logger;
    private readonly CollectorOptions _options;

    public UserCollector(
        IRsiApiClient apiClient,
        IUserRepository userRepo,
        IUserHandleHistoryRepository handleHistoryRepo,
        IUserEnrichmentQueueRepository queueRepo,
        IOrganizationMemberRepository memberRepo,
        IChangeEventRepository changeEventRepo,
        IUserChangeDetector userChangeDetector,
        UserProfileHtmlParser profileParser,
        ILogger<UserCollector> logger,
        IOptions<CollectorOptions> options)
    {
        _apiClient = apiClient;
        _userRepo = userRepo;
        _handleHistoryRepo = handleHistoryRepo;
        _queueRepo = queueRepo;
        _memberRepo = memberRepo;
        _changeEventRepo = changeEventRepo;
        _userChangeDetector = userChangeDetector;
        _profileParser = profileParser;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> EnrichAllUsersAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting user enrichment (Phase 4)");

        var processedCount = 0;
        var skippedCount = 0;
        var fetchBatchSize = Math.Max(1, _options.MaxConcurrentRequests) * 2;

        while (true)
        {
            var pending = await _queueRepo.GetPendingAsync(fetchBatchSize, _options.MaxEnrichmentAttempts, ct);
            if (pending.Count == 0)
            {
                _logger.LogInformation("Phase 4: no more users to enrich");
                break;
            }

            ct.ThrowIfCancellationRequested();

            // ── Fetch profiles concurrently ───────────────────────────────
            // GetUserProfileHtmlAsync handles its own semaphore (MaxConcurrentRequests slots)
            var fetchTasks = pending
                .Select(item => FetchProfileSafeAsync(item.UserHandle, ct))
                .ToList();
            var htmlResults = await Task.WhenAll(fetchTasks);

            // ── Process results sequentially (EF Core DbContext not thread-safe) ──
            for (int i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                var html = htmlResults[i];

                try
                {
                    if (string.IsNullOrEmpty(html))
                    {
                        await _queueRepo.IncrementAttemptAsync(item.Id, "Profile not found or fetch error", ct);
                        skippedCount++;
                        continue;
                    }

                    var success = await EnrichUserAsync(item.UserHandle, item.Priority >= 1, html, ct);
                    if (success)
                    {
                        await _queueRepo.MarkEnrichedAsync(item.Id, ct);
                        processedCount++;
                    }
                    else
                    {
                        await _queueRepo.IncrementAttemptAsync(item.Id, "Profile not found or parse error", ct);
                        skippedCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error enriching user {Handle}", item.UserHandle);
                    await _queueRepo.IncrementAttemptAsync(item.Id, ex.Message, ct);
                    skippedCount++;
                }
            }

            _logger.LogInformation(
                "Phase 4 progress: {Processed} enriched, {Skipped} skipped so far",
                processedCount, skippedCount);
        }

        _logger.LogInformation(
            "Phase 4 complete: {Count} users enriched, {Skipped} skipped",
            processedCount, skippedCount);
        return processedCount;
    }

    private async Task<string?> FetchProfileSafeAsync(string handle, CancellationToken ct)
    {
        try
        {
            return await _apiClient.GetUserProfileHtmlAsync(handle, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching profile for {Handle}", handle);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching profile for {Handle}", handle);
            return null;
        }
    }

    public async Task<bool> EnrichUserAsync(string handle, bool isNewHandle, string html, CancellationToken ct = default)
    {
        var profileData = _profileParser.ParseUserProfile(html);
        if (profileData == null)
        {
            _logger.LogWarning("Failed to parse profile for user {Handle}", handle);
            return false;
        }

        var timestamp = DateTime.UtcNow;
        var changeEvents = new List<ChangeEvent>();

        // Look up by citizen_id first (most reliable), then by handle
        var existingByCitizenId = profileData.CitizenId > 0
            ? await _userRepo.GetByCitizenIdAsync(profileData.CitizenId, ct)
            : null;
        var existingByHandle = await _userRepo.GetByHandleAsync(handle, ct);

        await using var transaction = await _userRepo.BeginTransactionAsync(ct);
        try
        {
            if (existingByCitizenId == null && existingByHandle == null)
            {
                // Truly new user — create and emit member_joined for all their orgs
                var newUser = new User
                {
                    CitizenId = profileData.CitizenId,
                    UserHandle = profileData.Handle,
                    DisplayName = profileData.DisplayName,
                    UrlImage = profileData.UrlImage,
                    Bio = profileData.Bio,
                    Location = profileData.Location,
                    Enlisted = profileData.Enlisted,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };
                await _userRepo.AddAsync(newUser, ct);

                if (profileData.CitizenId > 0)
                {
                    await _handleHistoryRepo.AddAsync(new UserHandleHistory
                    {
                        CitizenId = profileData.CitizenId,
                        UserHandle = profileData.Handle,
                        FirstSeen = timestamp,
                        LastSeen = timestamp
                    }, ct);

                    await _memberRepo.UpdateCitizenIdByHandleAsync(handle, profileData.CitizenId, ct);
                }

                if (isNewHandle)
                {
                    // Emit member_joined for each org this handle belongs to
                    var memberships = await _memberRepo.GetByUserHandleAsync(handle, ct);
                    foreach (var orgSid in memberships.Select(m => m.OrgSid).Distinct())
                    {
                        changeEvents.Add(new ChangeEvent
                        {
                            Timestamp = timestamp,
                            EntityType = "member",
                            EntityId = handle,
                            ChangeType = "member_joined",
                            OldValue = null,
                            NewValue = JsonSerializer.Serialize(new { Handle = handle, CitizenId = profileData.CitizenId }),
                            OrgSid = orgSid,
                            UserHandle = handle
                        });
                    }
                }

                _logger.LogDebug("New user {Handle} (citizen_id: {CitizenId})", handle, profileData.CitizenId);
            }
            else if (existingByCitizenId != null && existingByCitizenId.UserHandle != handle)
            {
                // Same citizen_id but different handle → rename
                var oldHandle = existingByCitizenId.UserHandle;

                var memberships = await _memberRepo.GetByUserHandleAsync(handle, ct);
                foreach (var orgSid in memberships.Select(m => m.OrgSid).Distinct())
                {
                    changeEvents.Add(new ChangeEvent
                    {
                        Timestamp = timestamp,
                        EntityType = "member",
                        EntityId = handle,
                        ChangeType = "handle_changed",
                        OldValue = oldHandle,
                        NewValue = handle,
                        OrgSid = orgSid,
                        UserHandle = handle
                    });
                }

                existingByCitizenId.UserHandle = profileData.Handle;
                existingByCitizenId.DisplayName = profileData.DisplayName;
                existingByCitizenId.UrlImage = profileData.UrlImage;
                existingByCitizenId.Bio = profileData.Bio;
                existingByCitizenId.Location = profileData.Location;
                existingByCitizenId.Enlisted = profileData.Enlisted;
                existingByCitizenId.UpdatedAt = timestamp;

                // Add handle history entry for the new handle
                var latestHistory = await _handleHistoryRepo.GetLatestAsync(profileData.CitizenId, ct);
                if (latestHistory == null || latestHistory.UserHandle != profileData.Handle)
                {
                    await _handleHistoryRepo.AddAsync(new UserHandleHistory
                    {
                        CitizenId = profileData.CitizenId,
                        UserHandle = profileData.Handle,
                        FirstSeen = timestamp,
                        LastSeen = timestamp
                    }, ct);
                }
                else
                {
                    latestHistory.LastSeen = timestamp;
                }

                await _memberRepo.UpdateCitizenIdByHandleAsync(handle, profileData.CitizenId, ct);

                _logger.LogInformation("Handle renamed: {OldHandle} → {NewHandle} (citizen_id: {CitizenId})",
                    oldHandle, handle, profileData.CitizenId);
            }
            else
            {
                // Known user, same handle — update info and detect changes
                var existingUser = existingByCitizenId ?? existingByHandle!;
                var userChanges = _userChangeDetector.DetectUserChanges(existingUser, profileData);

                existingUser.UserHandle = profileData.Handle;
                existingUser.DisplayName = profileData.DisplayName;
                existingUser.UrlImage = profileData.UrlImage;
                existingUser.Bio = profileData.Bio;
                existingUser.Location = profileData.Location;
                existingUser.Enlisted = profileData.Enlisted;
                existingUser.UpdatedAt = timestamp;

                if (profileData.CitizenId > 0)
                    await _memberRepo.UpdateCitizenIdByHandleAsync(handle, profileData.CitizenId, ct);

                changeEvents.AddRange(userChanges);

                _logger.LogDebug("Updated user {Handle} (citizen_id: {CitizenId})", handle, profileData.CitizenId);
            }

            if (changeEvents.Count > 0)
                await _changeEventRepo.AddRangeAsync(changeEvents, ct);

            await _userRepo.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return true;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}