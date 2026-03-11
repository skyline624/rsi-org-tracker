using Collector.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Services;

/// <summary>
/// Orchestrates the sequential execution of collection phases in a continuous loop.
/// </summary>
public class CollectionOrchestrator
{
    private readonly IOrganizationCollector _orgCollector;
    private readonly IMemberCollector _memberCollector;
    private readonly IUserCollector _userCollector;
    private readonly ILogger<CollectionOrchestrator> _logger;
    private readonly CollectorOptions _options;

    public CollectionOrchestrator(
        IOrganizationCollector orgCollector,
        IMemberCollector memberCollector,
        IUserCollector userCollector,
        ILogger<CollectionOrchestrator> logger,
        IOptions<CollectorOptions> options)
    {
        _orgCollector = orgCollector;
        _memberCollector = memberCollector;
        _userCollector = userCollector;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Executes the 4 phases in a continuous loop:
    /// 1. Discover organizations
    /// 2. Collect organization metadata
    /// 3. Collect members
    /// 4. Enrich user profiles
    /// </summary>
    public async Task RunCollectionLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting collection orchestrator");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("=== Beginning collection cycle ===");

                // Phase 1: Discover organizations
                _logger.LogInformation("Phase 1: Discovering organizations");
                var discoveredCount = await _orgCollector.DiscoverOrganizationsAsync(ct);
                _logger.LogInformation("Phase 1 complete: {Count} organizations discovered", discoveredCount);

                // Phase 2: Collect organization metadata
                _logger.LogInformation("Phase 2: Collecting organization metadata");
                var orgsProcessed = await _orgCollector.CollectOrganizationMetadataAsync(ct);
                _logger.LogInformation("Phase 2 complete: {Count} organizations processed", orgsProcessed);

                // Phase 3: Collect members
                _logger.LogInformation("Phase 3: Collecting members");
                var membersCollected = await _memberCollector.CollectAllMembersAsync(ct);
                _logger.LogInformation("Phase 3 complete: {Count} members collected", membersCollected);

                // Phase 4: Enrich user profiles
                _logger.LogInformation("Phase 4: Enriching user profiles");
                var usersEnriched = await _userCollector.EnrichAllUsersAsync(ct);
                _logger.LogInformation("Phase 4 complete: {Count} users enriched", usersEnriched);

                _logger.LogInformation("=== Collection cycle complete ===");

                // Wait before next cycle
                _logger.LogInformation("Waiting {Interval} before next cycle", _options.CycleInterval);
                await Task.Delay(_options.CycleInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Collection orchestrator stopped by cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during collection cycle");

                // Wait before retry after error
                _logger.LogInformation("Waiting {Delay} before retry", _options.ErrorDelay);
                await Task.Delay(_options.ErrorDelay, ct);
            }
        }

        _logger.LogInformation("Collection orchestrator stopped");
    }

    /// <summary>
    /// Runs a single collection cycle (useful for testing).
    /// </summary>
    public async Task RunSingleCycleAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Running single collection cycle");

        // Phase 1: Discover organizations
        _logger.LogInformation("Phase 1: Discovering organizations");
        var discoveredCount = await _orgCollector.DiscoverOrganizationsAsync(ct);
        _logger.LogInformation("Phase 1 complete: {Count} organizations discovered", discoveredCount);

        // Phase 2: Collect organization metadata
        _logger.LogInformation("Phase 2: Collecting organization metadata");
        var orgsProcessed = await _orgCollector.CollectOrganizationMetadataAsync(ct);
        _logger.LogInformation("Phase 2 complete: {Count} organizations processed", orgsProcessed);

        // Phase 3: Collect members
        _logger.LogInformation("Phase 3: Collecting members");
        var membersCollected = await _memberCollector.CollectAllMembersAsync(ct);
        _logger.LogInformation("Phase 3 complete: {Count} members collected", membersCollected);

        // Phase 4: Enrich user profiles
        _logger.LogInformation("Phase 4: Enriching user profiles");
        var usersEnriched = await _userCollector.EnrichAllUsersAsync(ct);
        _logger.LogInformation("Phase 4 complete: {Count} users enriched", usersEnriched);

        _logger.LogInformation("Single collection cycle complete");
    }
}