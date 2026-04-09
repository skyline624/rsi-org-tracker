using Collector.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Services;

/// <summary>
/// Orchestrates the sequential execution of the 4 collection phases. A single method
/// <see cref="RunCycleAsync"/> runs one cycle; <see cref="RunLoopAsync"/> wraps it
/// in a continuous loop honouring the configured cycle interval.
///
/// Phases are declared once as an ordered list of <see cref="Phase"/> delegates so
/// that each phase keeps its own name + skip predicate without duplicating the
/// try/catch scaffolding at every call site (DRY).
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
    /// Represents a named phase of the collection pipeline. <paramref name="Run"/>
    /// returns the number of units processed so we can surface a meaningful count
    /// in the completion log.
    /// </summary>
    private record Phase(string Name, Func<CancellationToken, Task<int>> Run, string ResultLabel);

    private Phase[] BuildPipeline(bool skipPhase2) => new[]
    {
        new Phase("Phase 1: Discovering organizations", _orgCollector.DiscoverOrganizationsAsync, "organizations discovered"),
        new Phase("Phase 2: Collecting organization metadata",
            skipPhase2
                ? _ => Task.FromResult(-1)
                : _orgCollector.CollectOrganizationMetadataAsync,
            "organizations processed"),
        new Phase("Phase 3: Collecting members", _memberCollector.CollectAllMembersAsync, "members collected"),
        new Phase("Phase 4: Enriching user profiles", _userCollector.EnrichAllUsersAsync, "users enriched"),
    };

    /// <summary>
    /// Runs a single full cycle.
    /// </summary>
    public async Task RunSingleCycleAsync(CancellationToken ct = default, bool skipPhase2 = false)
    {
        _logger.LogInformation("Running single collection cycle");
        await RunCycleAsync(ct, skipPhase2);
        _logger.LogInformation("Single collection cycle complete");
    }

    /// <summary>
    /// Runs the collection loop indefinitely until the token is cancelled.
    /// </summary>
    public async Task RunCollectionLoopAsync(CancellationToken ct, bool skipPhase2 = false)
    {
        _logger.LogInformation("Starting collection orchestrator");

        while (!ct.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;
            try
            {
                _logger.LogInformation("=== Beginning collection cycle ===");
                await RunCycleAsync(ct, skipPhase2);
                _logger.LogInformation("=== Collection cycle complete ===");

                // Compensate the wait so the effective period is the full CycleInterval,
                // not CycleInterval + cycle duration.
                var elapsed = DateTime.UtcNow - cycleStart;
                var wait = _options.CycleInterval - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    _logger.LogInformation("Waiting {Interval} before next cycle", wait);
                    await Task.Delay(wait, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "Cycle duration ({Elapsed}) exceeded CycleInterval ({Interval}) — starting next cycle immediately",
                        elapsed, _options.CycleInterval);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Collection orchestrator stopped by cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during collection cycle");
                _logger.LogInformation("Waiting {Delay} before retry", _options.ErrorDelay);
                await Task.Delay(_options.ErrorDelay, ct);
            }
        }

        _logger.LogInformation("Collection orchestrator stopped");
    }

    /// <summary>
    /// Executes every phase in order. Each phase runs inside a shared try/catch so
    /// a failure in one phase doesn't abort the cycle; only cancellation is fatal.
    /// </summary>
    private async Task RunCycleAsync(CancellationToken ct, bool skipPhase2)
    {
        foreach (var phase in BuildPipeline(skipPhase2))
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation(phase.Name);
            try
            {
                var count = await phase.Run(ct);
                if (count < 0)
                {
                    _logger.LogInformation("{Phase}: skipped", phase.Name);
                }
                else
                {
                    _logger.LogInformation("{Phase} complete: {Count} {Label}",
                        phase.Name, count, phase.ResultLabel);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Phase} failed — continuing to the next phase", phase.Name);
            }
        }
    }
}
