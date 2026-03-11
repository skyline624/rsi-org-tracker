using System.Net.Http;
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
    /// Enriches a single user profile.
    /// </summary>
    Task<bool> EnrichUserAsync(string handle, CancellationToken ct = default);
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
        var batchSize = _options.BatchSize;

        while (true)
        {
            var pending = await _queueRepo.GetPendingAsync(batchSize, ct);
            if (pending.Count == 0)
            {
                _logger.LogInformation("No more users to enrich");
                break;
            }

            // Use transaction for batch consistency
            await using var transaction = await _userRepo.BeginTransactionAsync(ct);
            try
            {
                foreach (var item in pending)
                {
                    try
                    {
                        var success = await EnrichUserAsync(item.UserHandle, ct);
                        if (success)
                        {
                            await _queueRepo.MarkEnrichedAsync(item.Id, ct);
                            processedCount++;
                        }
                        else
                        {
                            await _queueRepo.IncrementAttemptAsync(item.Id, "Profile not found or parse error", ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Propagate cancellation
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogWarning(ex, "HTTP error enriching user {Handle}", item.UserHandle);
                        await _queueRepo.IncrementAttemptAsync(item.Id, ex.Message, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error enriching user {Handle}", item.UserHandle);
                        await _queueRepo.IncrementAttemptAsync(item.Id, ex.Message, ct);
                    }
                }

                await _userRepo.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }

        _logger.LogInformation("User enrichment complete: {Count} users processed", processedCount);
        return processedCount;
    }

    public async Task<bool> EnrichUserAsync(string handle, CancellationToken ct = default)
    {
        var html = await _apiClient.GetUserProfileHtmlAsync(handle, ct);
        if (string.IsNullOrEmpty(html))
        {
            _logger.LogWarning("No profile HTML returned for user {Handle}", handle);
            return false;
        }

        var profileData = _profileParser.ParseUserProfile(html);
        if (profileData == null)
        {
            _logger.LogWarning("Failed to parse profile for user {Handle}", handle);
            return false;
        }

        // Get existing user
        var existingUser = profileData.CitizenId > 0
            ? await _userRepo.GetByCitizenIdAsync(profileData.CitizenId, ct)
            : await _userRepo.GetByHandleAsync(handle, ct);

        var timestamp = DateTime.UtcNow;

        // Use transaction for data consistency
        await using var transaction = await _userRepo.BeginTransactionAsync(ct);
        try
        {
            if (existingUser == null)
            {
                // Create new user
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

                // Add handle history entry
                if (profileData.CitizenId > 0)
                {
                    var handleHistory = new UserHandleHistory
                    {
                        CitizenId = profileData.CitizenId,
                        UserHandle = profileData.Handle,
                        FirstSeen = timestamp,
                        LastSeen = timestamp
                    };

                    await _handleHistoryRepo.AddAsync(handleHistory, ct);
                }

                _logger.LogDebug("Created new user {Handle} (citizen_id: {CitizenId})",
                    handle, profileData.CitizenId);
            }
            else
            {
                // Update existing user and detect changes
                var changes = _userChangeDetector.DetectUserChanges(existingUser, profileData);

                existingUser.UserHandle = profileData.Handle;
                existingUser.DisplayName = profileData.DisplayName;
                existingUser.UrlImage = profileData.UrlImage;
                existingUser.Bio = profileData.Bio;
                existingUser.Location = profileData.Location;
                existingUser.Enlisted = profileData.Enlisted;
                existingUser.UpdatedAt = timestamp;

                // Update handle history if handle changed
                if (profileData.CitizenId > 0 && profileData.Handle != existingUser.UserHandle)
                {
                    var handleHistory = await _handleHistoryRepo.GetLatestAsync(profileData.CitizenId, ct);
                    if (handleHistory == null || handleHistory.UserHandle != profileData.Handle)
                    {
                        var newHistory = new UserHandleHistory
                        {
                            CitizenId = profileData.CitizenId,
                            UserHandle = profileData.Handle,
                            FirstSeen = timestamp,
                            LastSeen = timestamp
                        };

                        await _handleHistoryRepo.AddAsync(newHistory, ct);
                    }
                    else
                    {
                        handleHistory.LastSeen = timestamp;
                    }
                }

                // Save change events
                if (changes.Count > 0)
                {
                    await _changeEventRepo.AddRangeAsync(changes, ct);
                }

                _logger.LogDebug("Updated user {Handle} (citizen_id: {CitizenId})",
                    handle, profileData.CitizenId);
            }

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