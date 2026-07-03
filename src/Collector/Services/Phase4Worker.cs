using Collector.Data.Repositories;
using Collector.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Services;

/// <summary>
/// Drains <c>user_enrichment_queue</c> in the background, decoupled from the
/// Phase 1/2/3 cycle loop. After every batch the worker re-counts pending
/// entries; when the count drops below <see cref="CollectorOptions.Phase4MinPendingThreshold"/>
/// it sleeps for <see cref="CollectorOptions.Phase4IdleInterval"/> before
/// checking again.
/// </summary>
public class Phase4Worker : BackgroundService
{
    // Wait briefly after startup so the host finishes wiring and the first
    // cycle can claim DB write locks before we start polling the queue.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Phase4Worker> _logger;
    private readonly CollectorOptions _options;

    // Tracks whether the previous iteration was idle (below threshold or
    // poison-batch back-off). We log INFO on idle/active transitions and
    // demote repeated idle ticks to DEBUG to avoid 288 INFO lines/day when
    // the queue is small.
    private bool _wasIdle;

    public Phase4Worker(
        IServiceScopeFactory scopeFactory,
        ILogger<Phase4Worker> logger,
        IOptions<CollectorOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Phase4Worker starting (threshold={Threshold}, idle={Idle})",
            _options.Phase4MinPendingThreshold, _options.Phase4IdleInterval);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queueRepo = scope.ServiceProvider.GetRequiredService<IUserEnrichmentQueueRepository>();
                var userCollector = scope.ServiceProvider.GetRequiredService<IUserCollector>();

                var pending = await queueRepo.CountPendingAsync(
                    _options.MaxEnrichmentAttempts, stoppingToken);

                if (pending < _options.Phase4MinPendingThreshold)
                {
                    LogIdle(pending);
                    await Task.Delay(_options.Phase4IdleInterval, stoppingToken);
                    continue;
                }

                if (_wasIdle)
                {
                    _logger.LogInformation("Phase4Worker resuming (pending={Pending})", pending);
                    _wasIdle = false;
                }
                _logger.LogDebug("Phase4Worker draining batch (pending={Pending})", pending);
                var batch = await userCollector.EnrichBatchAsync(stoppingToken);

                // Back off ONLY on a genuinely dead batch: nothing was pulled (queue
                // drained under a concurrent writer) or EVERY fetched row failed at the
                // network layer (Cloudflare 403/429 burst). A batch that enriched, parked
                // 404s, or deferred "n/a" rows IS real progress — loop straight into the
                // next batch instead of sleeping, so the queue actually drains.
                var deadBatch = batch.Processed == 0 || batch.Failed == batch.Processed;
                if (deadBatch)
                {
                    _logger.LogWarning(
                        "Phase4Worker made no progress (pending={Pending}, failed={Failed}/{Processed}); idling {Idle} to avoid hot loop",
                        pending, batch.Failed, batch.Processed, _options.Phase4IdleInterval);
                    _wasIdle = true;
                    await Task.Delay(_options.Phase4IdleInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phase4Worker iteration failed; retrying after {Delay}", _options.ErrorDelay);
                try { await Task.Delay(_options.ErrorDelay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Phase4Worker stopping");
    }

    private void LogIdle(int pending)
    {
        // Log INFO only on the active→idle transition; subsequent idle ticks
        // demote to DEBUG to keep operational logs readable.
        if (!_wasIdle)
        {
            _logger.LogInformation(
                "Phase4Worker idle: pending={Pending} below threshold {Threshold}, sleeping {Idle}",
                pending, _options.Phase4MinPendingThreshold, _options.Phase4IdleInterval);
            _wasIdle = true;
        }
        else
        {
            _logger.LogDebug(
                "Phase4Worker still idle: pending={Pending}",
                pending);
        }
    }
}
