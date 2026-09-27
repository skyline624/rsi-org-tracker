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
/// Per-outcome tally of a single <see cref="IUserCollector.EnrichBatchAsync"/> pass.
/// </summary>
/// <param name="Enriched">Profiles genuinely written to the users table.</param>
/// <param name="Gone">Handles that 404'd and were parked (never retried again).</param>
/// <param name="Deferred">Live "n/a" profiles, checked again in 14 days without spending an attempt.</param>
/// <param name="Failed">Transient/unparseable rows that spent a retry attempt.</param>
public readonly record struct EnrichBatchResult(int Enriched, int Gone, int Deferred, int Failed)
{
    /// <summary>Total rows pulled from the queue and handled in this batch.</summary>
    public int Processed => Enriched + Gone + Deferred + Failed;
}

/// <summary>
/// Per-outcome tally of a single <see cref="IUserCollector.RefreshProfilesAsync"/> pass.
/// </summary>
/// <param name="LastId">Id of the last citizen of the batch: the next batch starts after it.</param>
/// <param name="Refreshed">Profiles read and stored.</param>
/// <param name="Gone">Handles that answered 404: deleted, or given up by a renamed citizen.</param>
/// <param name="NoCitizenRecord">Live profiles without a citizen record.</param>
/// <param name="TakenOver">Handles now held by another citizen; the row is left as it is.</param>
/// <param name="Failed">Failed fetches and unreadable pages, read again on the next pass.</param>
public readonly record struct ProfileRefreshResult(
    long LastId, int Refreshed, int Gone, int NoCitizenRecord, int TakenOver, int Failed)
{
    /// <summary>Citizens handled in this batch.</summary>
    public int Processed => Refreshed + Gone + NoCitizenRecord + TakenOver + Failed;
}

/// <summary>
/// Interface for user enrichment operations.
/// </summary>
public interface IUserCollector
{
    /// <summary>
    /// Processes a single batch from the enrichment queue (sized by
    /// <c>MaxConcurrentRequests * 2</c>). Returns a per-outcome breakdown of the
    /// batch (enriched / gone / deferred / failed). An all-zero result means the
    /// queue was empty; a batch that only deferred "n/a" rows still counts as
    /// progress and is <b>not</b> a signal to back off.
    /// </summary>
    Task<EnrichBatchResult> EnrichBatchAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads again one batch of known citizens whose profile an older parser stored, the
    /// first ones after <paramref name="afterId"/>. The profile is stored as a reference
    /// read (no change event). A result with nothing processed means no such citizen is
    /// left after <paramref name="afterId"/>.
    /// </summary>
    Task<ProfileRefreshResult> RefreshProfilesAsync(long afterId, CancellationToken ct = default);

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

    public async Task<EnrichBatchResult> EnrichBatchAsync(CancellationToken ct = default)
    {
        var fetchBatchSize = Math.Max(1, _options.MaxConcurrentRequests) * 2;
        var pending = await _queueRepo.GetPendingAsync(fetchBatchSize, DateTime.UtcNow, ct);
        if (pending.Count == 0) return default;

        ct.ThrowIfCancellationRequested();

        // ── Fetch profiles concurrently ───────────────────────────────
        // The shared RsiRateGate caps concurrency (MaxConcurrentRequests) and pacing
        var fetchTasks = pending
            .Select(item => FetchProfileResultSafeAsync(item.UserHandle, ct))
            .ToList();
        var fetchResults = await Task.WhenAll(fetchTasks);

        // ── Process results sequentially (EF Core DbContext not thread-safe) ──
        var enriched = 0;   // genuinely written to the users table
        var gone = 0;       // 404 — settled, never retried
        var deferred = 0;   // live but "n/a" — checked again in 14 days, no attempt spent
        var failed = 0;     // transient/unparseable — retried after a backoff, up to the cap
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
                        await _queueRepo.MarkGoneAsync(item.Id, "Gone (HTTP 404)", DateTime.UtcNow, ct);
                        gone++;
                        break;

                    case UserProfileFetchOutcome.Failed:
                        // Transient (Cloudflare 403/429, network, retries exhausted) — retry.
                        await _queueRepo.RecordFailureAsync(item.Id, "Fetch failed (throttle/network)",
                            _options.MaxEnrichmentAttempts, DateTime.UtcNow, ct);
                        failed++;
                        break;

                    default: // Ok — body in hand, decide on parse outcome
                        var parsed = _profileParser.ParseProfile(fetch.Html!);
                        if (parsed.Outcome == ProfileParseOutcome.Success
                            && await EnrichUserCoreAsync(item.UserHandle, item.Priority >= 1, parsed.Data!, ct))
                        {
                            await _queueRepo.MarkEnrichedAsync(item.Id, DateTime.UtcNow, ct);
                            enriched++;
                        }
                        else if (parsed.Outcome == ProfileParseOutcome.NoCitizenNumber)
                        {
                            // Live profile that simply has no UEE Citizen Record yet
                            // ("n/a"). Not a failure: checked again later without
                            // counting an attempt, so we never permanently abandon it.
                            await _queueRepo.DeferAsync(item.Id, "No citizen record (n/a)", DateTime.UtcNow, ct);
                            deferred++;
                        }
                        else
                        {
                            await _queueRepo.RecordFailureAsync(item.Id, "Profile parse error",
                                _options.MaxEnrichmentAttempts, DateTime.UtcNow, ct);
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
                await _queueRepo.RecordFailureAsync(item.Id, ex.Message, _options.MaxEnrichmentAttempts, DateTime.UtcNow, ct);
                failed++;
            }
        }

        // With the queue draining continuously, a routine batch is "all deferred"
        // (n/a profiles). Log those at Debug to avoid tens of thousands of INFO
        // lines/day; keep INFO for batches that actually moved rows out of pending.
        var logLevel = (enriched + gone + failed) > 0
            ? LogLevel.Information
            : LogLevel.Debug;
        _logger.Log(
            logLevel,
            "Phase 4 batch: {Enriched} enriched, {Gone} gone(404), {Deferred} deferred(n/a), {Failed} failed (batch size {Size})",
            enriched, gone, deferred, failed, pending.Count);
        return new EnrichBatchResult(enriched, gone, deferred, failed);
    }

    public async Task<ProfileRefreshResult> RefreshProfilesAsync(long afterId, CancellationToken ct = default)
    {
        var batchSize = Math.Max(1, _options.MaxConcurrentRequests) * 2;
        var citizens = await _userRepo.GetProfilesToRefreshAsync(afterId, UserProfileHtmlParser.Version, batchSize, ct);
        if (citizens.Count == 0) return default;

        var fetchResults = await Task.WhenAll(citizens.Select(c => FetchProfileResultSafeAsync(c.UserHandle, ct)));

        int refreshed = 0, gone = 0, noCitizenRecord = 0, takenOver = 0, failed = 0;
        for (var i = 0; i < citizens.Count; i++)
        {
            var citizen = citizens[i];
            var fetch = fetchResults[i];
            try
            {
                if (fetch.Outcome == UserProfileFetchOutcome.NotFound)
                {
                    await _userRepo.MarkProfileReadAsync(citizen.Id, UserProfileHtmlParser.Version, ct);
                    gone++;
                    continue;
                }
                if (fetch.Outcome == UserProfileFetchOutcome.Failed)
                {
                    failed++;
                    continue;
                }

                var parsed = _profileParser.ParseProfile(fetch.Html!);
                switch (parsed.Outcome)
                {
                    case ProfileParseOutcome.NoCitizenNumber:
                        await _userRepo.MarkProfileReadAsync(citizen.Id, UserProfileHtmlParser.Version, ct);
                        noCitizenRecord++;
                        break;

                    case ProfileParseOutcome.Success when parsed.Data!.CitizenId != citizen.CitizenId:
                        // This citizen gave the handle up. Their row is fixed when their new
                        // handle shows up in a roster; the new holder, when Phase 4 reads it.
                        _logger.LogInformation(
                            "Profile refresh: {Handle} now belongs to citizen {CitizenId}, not {FormerCitizenId}; row left as it is",
                            citizen.UserHandle, parsed.Data.CitizenId, citizen.CitizenId);
                        await _userRepo.MarkProfileReadAsync(citizen.Id, UserProfileHtmlParser.Version, ct);
                        takenOver++;
                        break;

                    case ProfileParseOutcome.Success
                        when await EnrichUserCoreAsync(citizen.UserHandle, isNewHandle: false, parsed.Data!, ct):
                        refreshed++;
                        break;

                    default:
                        failed++;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error refreshing the profile of {Handle}", citizen.UserHandle);
                failed++;
            }
        }

        var result = new ProfileRefreshResult(citizens[^1].Id, refreshed, gone, noCitizenRecord, takenOver, failed);
        _logger.LogInformation(
            "Profile refresh batch: {Refreshed} refreshed, {Gone} gone(404), {NoRecord} n/a, {TakenOver} handle taken over, {Failed} failed (up to user {LastId})",
            refreshed, gone, noCitizenRecord, takenOver, failed, result.LastId);
        return result;
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

            // A handle given up and taken over by a citizen we do not know yet: that citizen
            // gets its own row; the former owner's row keeps the handle until it is read again.
            var handleTakenOver = existingByCitizenId == null && existingByHandle != null
                && profileData.CitizenId > 0 && existingByHandle.CitizenId > 0
                && existingByHandle.CitizenId != profileData.CitizenId;
            if (handleTakenOver)
            {
                _logger.LogInformation(
                    "Handle {Handle} now belongs to citizen {CitizenId}, not {FormerCitizenId}: new citizen row",
                    handle, profileData.CitizenId, existingByHandle!.CitizenId);
            }

            if (existingByCitizenId == null && (existingByHandle == null || handleTakenOver))
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
                    ParserVersion = UserProfileHtmlParser.Version,
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
                    foreach (var orgSid in await _memberRepo.GetOrgSidsForHandleAsync(handle, activeOnly: true, ct))
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

                foreach (var (orgSid, firstSeen) in await _memberRepo.GetFirstSeenByOrgAsync(handle, ct))
                {
                    // Phase 3 usually sees the new handle first: to it, the old one had left.
                    // A departure of the old handle once the new one was in the org is that
                    // same citizen, still there: the rename replaces it.
                    var falseDepartures = await _changeEventRepo.DeleteDeparturesSinceAsync(orgSid, oldHandle, firstSeen, ct);
                    if (falseDepartures > 0)
                    {
                        _logger.LogInformation(
                            "Rename {OldHandle} → {NewHandle}: removed {Count} member_left in {OrgSid}",
                            oldHandle, handle, falseDepartures, orgSid);
                    }

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

                ApplyProfile(existingByCitizenId, profileData, timestamp);

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
                // A profile last read by an older parser is a reference read: its stored
                // fields may have been misread, and a real change happened at an unknown time.
                var userChanges = existingUser.ParserVersion >= UserProfileHtmlParser.Version
                    ? _userChangeDetector.DetectUserChanges(existingUser, profileData)
                    : [];

                ApplyProfile(existingUser, profileData, timestamp);

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

    /// <summary>
    /// Copies a read profile onto the stored user. Every profile has a display name,
    /// an avatar and an enlistment date: when one of them cannot be read, the stored
    /// value is kept rather than erased. A bio or a location can be removed on RSI.
    /// </summary>
    private static void ApplyProfile(User user, UserProfileData profile, DateTime timestamp)
    {
        user.UserHandle = profile.Handle;
        user.DisplayName = profile.DisplayName ?? user.DisplayName;
        user.UrlImage = profile.UrlImage ?? user.UrlImage;
        user.Enlisted = profile.Enlisted ?? user.Enlisted;
        user.Bio = profile.Bio;
        user.Location = profile.Location;
        user.ParserVersion = UserProfileHtmlParser.Version;
        user.UpdatedAt = timestamp;
    }
}