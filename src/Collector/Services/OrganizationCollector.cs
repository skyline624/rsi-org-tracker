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
    private readonly ILogger<OrganizationCollector> _logger;
    private readonly CollectorOptions _options;

    public OrganizationCollector(
        IRsiApiClient apiClient,
        IDiscoveredOrganizationRepository discoveredRepo,
        IOrganizationRepository orgRepo,
        IChangeEventRepository changeEventRepo,
        IChangeDetector changeDetector,
        ILogger<OrganizationCollector> logger,
        IOptions<CollectorOptions> options)
    {
        _apiClient = apiClient;
        _discoveredRepo = discoveredRepo;
        _orgRepo = orgRepo;
        _changeEventRepo = changeEventRepo;
        _changeDetector = changeDetector;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> DiscoverOrganizationsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting organization discovery (Phase 1)");

        var allOrgs = await _apiClient.GetAllOrganizationsAsync(
            _options.DiscoverSortMethods,
            _options.OrganizationPageSize,
            ct);

        var newCount = 0;
        var timestamp = DateTime.UtcNow;

        foreach (var org in allOrgs)
        {
            var exists = await _discoveredRepo.ExistsAsync(org.Sid, ct);
            if (!exists)
            {
                var discovered = new DiscoveredOrganization
                {
                    Sid = org.Sid,
                    Name = org.Name,
                    UrlImage = org.UrlImage,
                    UrlCorpo = org.UrlCorpo,
                    DiscoveredAt = timestamp
                };

                await _discoveredRepo.AddAsync(discovered, ct);
                newCount++;
            }
        }

        await _discoveredRepo.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Discovery complete: {Total} organizations found, {New} new",
            allOrgs.Count, newCount);

        return allOrgs.Count;
    }

    public async Task<int> CollectOrganizationMetadataAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting organization metadata collection (Phase 2)");

        var discovered = await _discoveredRepo.GetAllUnprocessedAsync(ct);
        var processedCount = 0;

        // Get existing org snapshots for change detection
        var existingOrgs = await _orgRepo.GetAllLatestAsync(ct);
        var existingBySid = existingOrgs.ToDictionary(o => o.Sid);

        var timestamp = DateTime.UtcNow;
        var changeEvents = new List<ChangeEvent>();

        foreach (var discoveredOrg in discovered)
        {
            try
            {
                var previousSnapshot = existingBySid.TryGetValue(discoveredOrg.Sid, out var existing)
                    ? new OrganizationSnapshot
                    {
                        Sid = existing.Sid,
                        Name = existing.Name,
                        Archetype = existing.Archetype,
                        Commitment = existing.Commitment,
                        Recruiting = existing.Recruiting,
                        Roleplay = existing.Roleplay,
                        Lang = existing.Lang,
                        Members = existing.MembersCount
                    }
                    : null;

                // Create organization snapshot from discovered data
                var currentSnapshot = new OrganizationSnapshot
                {
                    Sid = discoveredOrg.Sid,
                    Name = discoveredOrg.Name,
                    // Note: Full metadata would require additional API call
                    // For now, we save what we have from discovery
                };

                // Save organization snapshot
                var org = new Organization
                {
                    Sid = discoveredOrg.Sid,
                    Name = discoveredOrg.Name,
                    UrlImage = discoveredOrg.UrlImage,
                    UrlCorpo = discoveredOrg.UrlCorpo,
                    Timestamp = timestamp
                };

                await _orgRepo.AddAsync(org, ct);

                // Detect changes
                var changes = _changeDetector.DetectOrganizationChanges(
                    previousSnapshot, currentSnapshot, discoveredOrg.Sid);
                changeEvents.AddRange(changes);

                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing organization {Sid}", discoveredOrg.Sid);
            }
        }

        // Save change events
        if (changeEvents.Count > 0)
        {
            await _changeEventRepo.AddRangeAsync(changeEvents, ct);
        }

        await _orgRepo.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Metadata collection complete: {Count} organizations processed, {Changes} changes detected",
            processedCount, changeEvents.Count);

        return processedCount;
    }
}