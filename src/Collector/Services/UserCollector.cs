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
/// Interface for user enrichment operations.
/// </summary>
public interface IUserCollector
{
    /// <summary>
    /// Phase 4 (legacy): drains the enrichment queue end-to-end. Kept as a thin
    /// loop around <see cref="EnrichBatchAsync"/> for one-shot/test use; live
    /// runs are driven by <c>Phase4Worker</c> instead.
    /// </summary>
    Task<int> EnrichAllUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Processes a single batch from the enrichment queue (sized by
    /// <c>MaxConcurrentRequests * 2</c>). Returns the number of profiles
    /// successfully enriched in this batch. Returns 0 when there is nothing
    /// pending or every pending row exceeded MaxEnrichmentAttempts.
    /// </summary>
    Task<int> EnrichBatchAsync(CancellationToken ct = default);

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
        _logger.LogInformation("Starting user enrichment (drain mode)");
        var totalProcessed = 0;
        while (true)
        {
            var processed = await EnrichBatchAsync(ct);
            if (processed == 0) break;
            totalProcessed += processed;
        }
        _logger.LogInformation("User enrichment drain complete: {Count} users enriched", totalProcessed);
        return totalProcessed;
    }

    public async Task<int> EnrichBatchAsync(CancellationToken ct = default)
    {
        var fetchBatchSize = Math.Max(1, _options.MaxConcurrentRequests) * 2;
        var pending = await _queueRepo.GetPendingAsync(fetchBatchSize, _options.MaxEnrichmentAttempts, ct);
        if (pending.Count == 0) return 0;

        ct.ThrowIfCancellationRequested();

        // ── Fetch profiles concurrently ───────────────────────────────
        // GetUserProfileResultAsync handles its own semaphore (MaxConcurrentRequests slots)
        var fetchTasks = pending
            .Select(item => FetchProfileResultSafeAsync(item.UserHandle, ct))
            .ToList();
        var fetchResults = await Task.WhenAll(fetchTasks);

        // ── Process results sequentially (EF Core DbContext not thread-safe) ──
        var enriched = 0;   // genuinely written to the users table
        var gone = 0;       // 404 — parked, never retried again
        var deferred = 0;   // live but "n/a" — pushed to the back, no attempt spent
        var failed = 0;     // transient/unparseable — counts towards the retry cap
        for (int i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            var fetch = fetchResults[i];

            try
            {
                switch (fetch.Outcome)
                {
                    case UserProfileFetchOutcome.NotFound:
                        // 404 → handle is gone or renamed. Stop retrying immediately
                        // instead of burning MaxEnrichmentAttempts fetches on a dead URL.
                        await _queueRepo.MarkGoneAsync(item.Id, "Gone (HTTP 404)", ct);
                        gone++;
                        break;

                    case UserProfileFetchOutcome.Failed:
                        // Transient (Cloudflare 403/429, network, retries exhausted) — retry.
                        await _queueRepo.IncrementAttemptAsync(item.Id, "Fetch failed (throttle/network)", ct);
                        failed++;
                        break;

                    default: // Ok — body in hand, decide on parse outcome
                        var parsed = _profileParser.ParseProfile(fetch.Html!);
                        if (parsed.Outcome == ProfileParseOutcome.Success
                            && await EnrichUserCoreAsync(item.UserHandle, item.Priority >= 1, parsed.Data!, ct))
                        {
                            await _queueRepo.MarkEnrichedAsync(item.Id, ct);
                            enriched++;
                        }
                        else if (parsed.Outcome == ProfileParseOutcome.NoCitizenNumber)
                        {
                            // Live profile that simply has no UEE Citizen Record yet
                            // ("n/a"). Not a failure: defer for a later pass instead of
                            // counting an attempt, so we never permanently abandon it.
                            await _queueRepo.DeferAsync(item.Id, "No citizen record (n/a)", ct);
                            deferred++;
                        }
                        else
                        {
                            await _queueRepo.IncrementAttemptAsync(item.Id, "Profile parse error", ct);
                            failed++;
                        }
                        break;
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
                failed++;
            }
        }

        _logger.LogInformation(
            "Phase 4 batch: {Enriched} enriched, {Gone} gone(404), {Deferred} deferred(n/a), {Failed} failed (batch size {Size})",
            enriched, gone, deferred, failed, pending.Count);
        return enriched;
    }

    private async Task<UserProfileFetchResult> FetchProfileResultSafeAsync(string handle, CancellationToken ct)
    {
        try
        {
            return await _apiClient.GetUserProfileResultAsync(handle, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching profile for {Handle}", handle);
            return new UserProfileFetchResult(null, UserProfileFetchOutcome.Failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching profile for {Handle}", handle);
            return new UserProfileFetchResult(null, UserProfileFetchOutcome.Failed);
        }
    }

    // RSI handles are URL-safe ASCII (letters, digits, underscore, dash, 3–30
    // chars). Anything outside that shape almost certainly means the parser
    // grabbed a UI label by mistake — refuse to persist it.
    private static readonly System.Text.RegularExpressions.Regex HandleShape =
        new(@"^[A-Za-z0-9_-]{3,50}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<bool> EnrichUserAsync(string handle, bool isNewHandle, string html, CancellationToken ct = default)
    {
        var parsed = _profileParser.ParseProfile(html);
        if (parsed.Outcome != ProfileParseOutcome.Success || parsed.Data is null)
        {
            _logger.LogWarning("Failed to parse profile for user {Handle}", handle);
            return false;
        }

        return await EnrichUserCoreAsync(handle, isNewHandle, parsed.Data, ct);
    }

    /// <summary>
    /// Persists an already-parsed profile (create/rename/update + change events).
    /// Split out from <see cref="EnrichUserAsync(string,bool,string,CancellationToken)"/>
    /// so the batch loop can branch on the parse outcome without parsing twice.
    /// </summary>
    private async Task<bool> EnrichUserCoreAsync(string handle, bool isNewHandle, UserProfileData profileData, CancellationToken ct)
    {
        // Defence in depth — the parser's IsHandleShape filter is the first
        // line, this is the second. Without it a future parser regression
        // could re-introduce the "CITIZEN DOSSIER" corruption that overwrote
        // ~78k users rows in the past.
        if (!HandleShape.IsMatch(profileData.Handle))
        {
            _logger.LogWarning(
                "Rejecting profile for {Handle}: parsed handle '{Parsed}' is not URL-safe (parser regression?)",
                handle, profileData.Handle);
            return false;
        }

        var timestamp = DateTime.UtcNow;
        var changeEvents = new List<ChangeEvent>();

        // Open the transaction FIRST so lookups and mutations happen on a consistent
        // snapshot. Any exception below triggers a rollback AND clears the DbContext
        // change tracker so partially-mutated entities don't leak into future calls
        // that share the same scoped DbContext.
        await using var transaction = await _userRepo.BeginTransactionAsync(ct);
        try
        {
            // citizen_id is the permanent key; handle is ambiguous. Look up both so we
            // can detect (a) renames, (b) handle reuse between two distinct citizens,
            // (c) brand-new users.
            var existingByCitizenId = profileData.CitizenId > 0
                ? await _userRepo.GetByCitizenIdAsync(profileData.CitizenId, ct)
                : null;
            var existingByHandle = await _userRepo.GetByHandleAsync(handle, ct);

            var sameEntity = existingByCitizenId != null
                && existingByHandle != null
                && existingByCitizenId.Id == existingByHandle.Id;

            // Detect the "handle reuse" collision: we have a user A with citizen_id X
            // already in the DB, and a DIFFERENT user B currently also holding handle H.
            // The newly-fetched profile says X now uses H, so B must have been renamed
            // off-band. We can't know B's new handle yet, so we log and skip B for this
            // pass — Phase 4 will sweep them into the queue on the next cycle.
            if (existingByCitizenId != null && existingByHandle != null && !sameEntity)
            {
                _logger.LogWarning(
                    "Handle reuse detected for {Handle}: citizen_id {CitizenIdNew} claims it, " +
                    "but {StaleUserId} (citizen_id {CitizenIdStale}) still holds it in DB. " +
                    "Stale user will be refreshed on its next enrichment pass.",
                    handle, profileData.CitizenId, existingByHandle.Id, existingByHandle.CitizenId);
            }

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
                // Same citizen_id but different handle → rename. Always prefer
                // existingByCitizenId as the source of truth; any entity returned by
                // GetByHandleAsync is ignored here because it may point at a stale
                // reuse of the handle by another (soon-to-be-updated) user.
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
                // Known user, same handle — update info and detect changes. Prefer
                // existingByCitizenId (permanent key) over existingByHandle whenever
                // both are set, to defend against handle-reuse edge cases.
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
            // Drop any in-memory mutations so the shared scoped DbContext does not
            // leak partial state into the next handle we process.
            _userRepo.ClearTrackedEntities();
            throw;
        }
    }
}