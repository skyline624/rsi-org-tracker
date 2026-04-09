using Collector.Data.Repositories;
using Collector.Dtos;
using Collector.Models;
using Collector.Options;
using Collector.Parsers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Services;

/// <summary>
/// Interface for organization collection operations.
/// </summary>
public interface IOrganizationCollector
{
    /// <summary>
    /// Phase 1: Discovers all organizations from the RSI API.
    /// </summary>
    Task<int> DiscoverOrganizationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 2: Collects metadata for discovered organizations.
    /// </summary>
    Task<int> CollectOrganizationMetadataAsync(CancellationToken ct = default);
}

/// <summary>
/// Collects organization data from the RSI API.
/// </summary>
public class OrganizationCollector : IOrganizationCollector
{
    private readonly IRsiApiClient _apiClient;
    private readonly IDiscoveredOrganizationRepository _discoveredRepo;
    private readonly IOrganizationRepository _orgRepo;
    private readonly IChangeEventRepository _changeEventRepo;
    private readonly IChangeDetector _changeDetector;
    private readonly OrgPageHtmlParser _orgPageParser;
    private readonly ILogger<OrganizationCollector> _logger;
    private readonly CollectorOptions _options;

    public OrganizationCollector(
        IRsiApiClient apiClient,
        IDiscoveredOrganizationRepository discoveredRepo,
        IOrganizationRepository orgRepo,
        IChangeEventRepository changeEventRepo,
        IChangeDetector changeDetector,
        OrgPageHtmlParser orgPageParser,
        ILogger<OrganizationCollector> logger,
        IOptions<CollectorOptions> options)
    {
        _apiClient = apiClient;
        _discoveredRepo = discoveredRepo;
        _orgRepo = orgRepo;
        _changeEventRepo = changeEventRepo;
        _changeDetector = changeDetector;
        _orgPageParser = orgPageParser;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> DiscoverOrganizationsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting organization discovery (Phase 1)");

        // Load existing SIDs once to avoid duplicates
        var existingSids = await _discoveredRepo.GetAllSidsAsync(ct);
        var existingSidSet = new HashSet<string>(existingSids, StringComparer.OrdinalIgnoreCase);

        // Load existing org snapshots for change detection
        var existingOrgs = await _orgRepo.GetAllLatestAsync(ct);
        var existingBySid = existingOrgs.ToDictionary(o => o.Sid, StringComparer.OrdinalIgnoreCase);

        // Track SIDs already saved to organizations in this run (multiple sort methods can return same org)
        var savedOrgSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var totalNew = 0;
        var timestamp = DateTime.UtcNow;
        var changeEvents = new List<ChangeEvent>();

        foreach (var sortMethod in _options.DiscoverSortMethods)
        {
            _logger.LogInformation("Discovering organizations with sort: {SortMethod}", sortMethod);

            var page = 1;
            var emptyPages = 0;
            var pagesSinceLastSave = 0;
            var maxPages = _options.DiscoveryMaxPages > 0 ? _options.DiscoveryMaxPages : int.MaxValue;

            while (emptyPages < _options.EmptyPagesThreshold && page <= maxPages)
            {
                var orgs = await _apiClient.GetOrganizationsAsync(
                    page, "", sortMethod, _options.OrganizationPageSize, ct);

                if (orgs == null || orgs.Count == 0)
                {
                    emptyPages++;
                    page++;
                    continue;
                }

                emptyPages = 0;

                foreach (var orgData in orgs)
                {
                    // Add to discovered_organizations if new
                    if (!existingSidSet.Contains(orgData.Sid))
                    {
                        await _discoveredRepo.AddAsync(new DiscoveredOrganization
                        {
                            Sid = orgData.Sid,
                            Name = orgData.Name,
                            UrlImage = orgData.UrlImage,
                            UrlCorpo = orgData.UrlCorpo,
                            DiscoveredAt = timestamp
                        }, ct);
                        existingSidSet.Add(orgData.Sid);
                        totalNew++;
                    }

                    // Save basic metadata to organizations table (once per cycle per SID)
                    if (!savedOrgSids.Contains(orgData.Sid))
                    {
                        var org = new Organization
                        {
                            Sid          = orgData.Sid,
                            Name         = orgData.Name,
                            UrlImage     = orgData.UrlImage,
                            UrlCorpo     = orgData.UrlCorpo,
                            Archetype    = orgData.Archetype,
                            Lang         = orgData.Lang,
                            Commitment   = orgData.Commitment,
                            Recruiting   = orgData.Recruiting,
                            Roleplay     = orgData.Roleplay,
                            MembersCount = orgData.MembersCount,
                            Timestamp    = timestamp,
                            ContentCollected = false
                        };
                        await _orgRepo.AddAsync(org, ct);
                        savedOrgSids.Add(orgData.Sid);

                        // Detect changes vs previous snapshot
                        if (existingBySid.TryGetValue(orgData.Sid, out var prev))
                        {
                            var prevSnap = new OrganizationSnapshot
                            {
                                Sid = prev.Sid, Name = prev.Name, Archetype = prev.Archetype,
                                Commitment = prev.Commitment, Recruiting = prev.Recruiting,
                                Roleplay = prev.Roleplay, Lang = prev.Lang, Members = prev.MembersCount
                            };
                            var currSnap = new OrganizationSnapshot
                            {
                                Sid = orgData.Sid, Name = orgData.Name, Archetype = orgData.Archetype,
                                Commitment = orgData.Commitment, Recruiting = orgData.Recruiting,
                                Roleplay = orgData.Roleplay, Lang = orgData.Lang, Members = orgData.MembersCount
                            };
                            changeEvents.AddRange(_changeDetector.DetectOrganizationChanges(prevSnap, currSnap, orgData.Sid));
                        }
                    }
                }

                pagesSinceLastSave++;
                page++;

                // Save every 10 pages
                if (pagesSinceLastSave >= 10)
                {
                    if (changeEvents.Count > 0)
                    {
                        await _changeEventRepo.AddRangeAsync(changeEvents, ct);
                        changeEvents.Clear();
                    }
                    await _discoveredRepo.SaveChangesAsync(ct);
                    await _orgRepo.SaveChangesAsync(ct);
                    pagesSinceLastSave = 0;
                    _logger.LogInformation(
                        "Discovery progress: page {Page}, {Total} orgs known ({New} new), {Saved} metadata saved",
                        page - 1, existingSidSet.Count, totalNew, savedOrgSids.Count);
                }
            }

            // Save remaining
            if (pagesSinceLastSave > 0)
            {
                if (changeEvents.Count > 0)
                {
                    await _changeEventRepo.AddRangeAsync(changeEvents, ct);
                    changeEvents.Clear();
                }
                await _discoveredRepo.SaveChangesAsync(ct);
                await _orgRepo.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation(
            "Discovery complete: {Total} orgs known, {New} new, {Saved} metadata snapshots saved, {Changes} changes",
            existingSidSet.Count, totalNew, savedOrgSids.Count, changeEvents.Count);

        return existingSidSet.Count;
    }

    public async Task<int> CollectOrganizationMetadataAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting extended content collection (Phase 2)");

        // Only process orgs not yet enriched with content this refresh interval.
        // Deduplicate by SID — the source query may return duplicates when an org
        // is discovered by multiple sort methods, and we must never call AddAsync
        // twice for the same SID within a single cycle (that creates two snapshots
        // with identical Timestamps and poisons the EF change tracker).
        var since = DateTime.UtcNow.AddHours(-_options.MetadataRefreshIntervalHours);
        var rawStale = await _discoveredRepo.GetStaleAsync(since, ct);
        var stale = rawStale
            .GroupBy(d => d.Sid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (stale.Count != rawStale.Count)
        {
            _logger.LogWarning(
                "Phase 2 deduped stale orgs: {Raw} → {Deduped}",
                rawStale.Count, stale.Count);
        }
        var total = stale.Count;

        if (total == 0)
        {
            _logger.LogInformation("Phase 2: all organizations have fresh content — nothing to do");
            return 0;
        }

        _logger.LogInformation(
            "Phase 2: {Total} organizations need extended content (older than {Hours}h)",
            total, _options.MetadataRefreshIntervalHours);

        // Load latest snapshots to update ContentCollected flag
        var latestOrgs = await _orgRepo.GetLatestBySidsAsync(stale.Select(d => d.Sid), ct);

        var processedCount = 0;
        var skippedCount = 0;
        var changeEvents = new List<ChangeEvent>();
        var timestamp = DateTime.UtcNow;

        // Batch size: fetch N pages concurrently, then write to DB sequentially
        // N = MaxConcurrentRequests (default 5), kept slightly larger to keep pipeline full
        var batchSize = Math.Max(1, _options.MaxConcurrentRequests) * 2;

        for (int batchStart = 0; batchStart < stale.Count; batchStart += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = stale.Skip(batchStart).Take(batchSize).ToList();

            // ── Fetch pages concurrently ──────────────────────────────────
            // GetOrgPageHtmlAsync handles its own semaphore (MaxConcurrentRequests slots)
            var fetchTasks = batch.Select(org => FetchPageSafeAsync(org.Sid, ct)).ToList();
            var htmlResults = await Task.WhenAll(fetchTasks);

            // ── Write results sequentially (EF Core DbContext not thread-safe) ──
            for (int i = 0; i < batch.Count; i++)
            {
                var discoveredOrg = batch[i];
                var html = htmlResults[i];

                if (html == null)
                {
                    skippedCount++;
                    continue;
                }

                var pageData = _orgPageParser.Parse(html, discoveredOrg.Sid);
                if (pageData == null)
                {
                    _logger.LogWarning("Could not parse org page for {Sid}", discoveredOrg.Sid);
                    skippedCount++;
                    continue;
                }

                if (latestOrgs.TryGetValue(discoveredOrg.Sid, out var existing))
                {
                    changeEvents.AddRange(DetectContentChanges(existing, pageData, timestamp));

                    await _orgRepo.AddAsync(new Organization
                    {
                        Sid              = existing.Sid,
                        Name             = existing.Name,
                        UrlImage         = existing.UrlImage,
                        UrlCorpo         = existing.UrlCorpo,
                        Archetype        = existing.Archetype,
                        Lang             = existing.Lang,
                        Commitment       = existing.Commitment,
                        Recruiting       = existing.Recruiting,
                        Roleplay         = existing.Roleplay,
                        MembersCount     = existing.MembersCount,
                        Timestamp        = timestamp,
                        ContentCollected = true,
                        Description      = pageData.Description,
                        History          = pageData.History,
                        Manifesto        = pageData.Manifesto,
                        Charter          = pageData.Charter,
                        FocusPrimaryName    = pageData.FocusPrimaryName,
                        FocusPrimaryImage   = pageData.FocusPrimaryImage,
                        FocusSecondaryName  = pageData.FocusSecondaryName,
                        FocusSecondaryImage = pageData.FocusSecondaryImage
                    }, ct);
                }

                processedCount++;
            }

            // Atomically persist: all member snapshots of the batch AND their change
            // events in one transaction. On failure we roll back, clear the change
            // tracker, and skip the batch (the orgs stay "stale" so the next cycle
            // retries them).
            await using (var batchTx = await _orgRepo.BeginTransactionAsync(ct))
            {
                try
                {
                    if (changeEvents.Count > 0)
                    {
                        await _changeEventRepo.AddRangeAsync(changeEvents, ct);
                    }
                    await _orgRepo.SaveChangesAsync(ct);
                    await batchTx.CommitAsync(ct);
                    changeEvents.Clear();
                }
                catch (Exception ex)
                {
                    await batchTx.RollbackAsync(ct);
                    _orgRepo.ClearTrackedEntities();
                    changeEvents.Clear();
                    _logger.LogError(ex,
                        "Phase 2 batch persist failed around batch {BatchStart}; orgs will retry next cycle",
                        batchStart);
                    continue;
                }
            }

            _logger.LogInformation(
                "Phase 2 progress: {Processed}/{Total} enriched, {Skipped} skipped",
                processedCount, total, skippedCount);
        }

        _logger.LogInformation(
            "Phase 2 complete: {Count}/{Total} organizations enriched, {Skipped} skipped, {Changes} content changes",
            processedCount, total, skippedCount, changeEvents.Count);

        return processedCount;
    }

    private async Task<string?> FetchPageSafeAsync(string sid, CancellationToken ct)
    {
        try
        {
            return await _apiClient.GetOrgPageHtmlAsync(sid, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching org page for {Sid}", sid);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching org page for {Sid}", sid);
            return null;
        }
    }

    private static IEnumerable<ChangeEvent> DetectContentChanges(
        Organization previous, OrgPageData current, DateTime timestamp)
    {
        var events = new List<ChangeEvent>();

        void Check(string field, string? prev, string? curr)
        {
            if (prev != curr && !(string.IsNullOrEmpty(prev) && string.IsNullOrEmpty(curr)))
                events.Add(new ChangeEvent
                {
                    Timestamp  = timestamp,
                    EntityType = "organization",
                    EntityId   = current.Sid,
                    ChangeType = $"{field}_changed",
                    OldValue   = prev,
                    NewValue   = curr,
                    OrgSid     = current.Sid
                });
        }

        Check("description", previous.Description, current.Description);
        Check("history",     previous.History,     current.History);
        Check("manifesto",   previous.Manifesto,   current.Manifesto);
        Check("charter",     previous.Charter,     current.Charter);
        Check("focus_primary",   previous.FocusPrimaryName,   current.FocusPrimaryName);
        Check("focus_secondary", previous.FocusSecondaryName, current.FocusSecondaryName);

        return events;
    }
}