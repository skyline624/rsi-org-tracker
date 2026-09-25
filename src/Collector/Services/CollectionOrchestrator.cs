using Collector.Options;
using Microsoft.Extensions.DependencyInjection;
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
///
/// Each phase runs in its own DI scope, so the DbContext (and every row it
/// tracked) is released when the phase ends instead of living as long as the process.
/// </summary>
public class CollectionOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CollectionOrchestrator> _logger;
    private readonly CollectorOptions _options;

    public CollectionOrchestrator(
        IServiceScopeFactory scopeFactory,
        ILogger<CollectionOrchestrator> logger,
        IOptions<CollectorOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Represents a named phase of the collection pipeline. <paramref name="Run"/>
    /// returns the number of units processed so we can surface a meaningful count
    /// in the completion log.
    /// </summary>
    private record Phase(string Name, Func<CancellationToken, Task<int>> Run, string ResultLabel);

    // Phase 4 (user enrichment) is no longer part of the cycle pipeline — it
    // runs continuously in Phase4Worker, decoupled so a deep enrichment queue
    // can't freeze Phase 1/2/3 fresh-data refresh.
    private Phase[] BuildPipeline(bool skipPhase2) => new[]
    {
        new Phase("Phase 1: Discovering organizations",
            InScope<IOrganizationCollector>((c, ct) => c.DiscoverOrganizationsAsync(ct)),
            "organizations discovered"),
        new Phase("Phase 2: Collecting organization metadata",
            skipPhase2
                ? _ => Task.FromResult(-1)
                : InScope<IOrganizationCollector>((c, ct) => c.CollectOrganizationMetadataAsync(ct)),
            "organizations processed"),
        new Phase("Phase 3: Collecting members",
            InScope<IMemberCollector>((c, ct) => c.CollectAllMembersAsync(ct)),
            "members collected"),
    };

    private Func<CancellationToken, Task<int>> InScope<TService>(Func<TService, CancellationToken, Task<int>> run)
        where TService : notnull
        => async ct =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await run(scope.ServiceProvider.GetRequiredService<TService>(), ct);
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
                try { await Task.Delay(_options.ErrorDelay, ct); }
                catch (OperationCanceledException) { break; }
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
