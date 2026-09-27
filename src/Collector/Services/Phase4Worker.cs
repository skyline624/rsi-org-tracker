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
/// it reads again, at <see cref="CollectorOptions.ProfileRefreshPerHour"/>, the profiles
/// an older parser stored, and otherwise sleeps for <see cref="CollectorOptions.Phase4IdleInterval"/>
/// before checking again.
/// </summary>
public class Phase4Worker : BackgroundService
{
    // Wait briefly after startup so the host finishes wiring and the first
    // cycle can claim DB write locks before we start polling the queue.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>Wait after a pass that left nothing to read again, or only failures.</summary>
    private static readonly TimeSpan RefreshRecheck = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Phase4Worker> _logger;
    private readonly CollectorOptions _options;
    private readonly TimeProvider _time;

    // Tracks whether the previous iteration was idle (below threshold or
    // poison-batch back-off). We log INFO on idle/active transitions and
    // demote repeated idle ticks to DEBUG to avoid 288 INFO lines/day when
    // the queue is small.
    private bool _wasIdle;

    // The profile refresh walks the users table by Id. A pass ends when nothing is left
    // after the cursor; another one follows at once only to read again what failed. Kept
    // in memory: after a restart the walk starts over and skips the profiles already read.
    private long _refreshCursor;
    private int _settledInPass;
    private int _failedInPass;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MinValue;

    public Phase4Worker(
        IServiceScopeFactory scopeFactory,
        ILogger<Phase4Worker> logger,
        IOptions<CollectorOptions> options,
        TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Phase4Worker starting (threshold={Threshold}, idle={Idle}, profile refresh={PerHour}/h)",
            _options.Phase4MinPendingThreshold, _options.Phase4IdleInterval, _options.ProfileRefreshPerHour);

        try { await Task.Delay(StartupDelay, _time, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = await RunOnceAsync(stoppingToken);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phase4Worker iteration failed; retrying after {Delay}", _options.ErrorDelay);
                try { await Task.Delay(_options.ErrorDelay, _time, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Phase4Worker stopping");
    }

    /// <summary>
    /// One iteration: a queue batch while the queue is busy, else a profile refresh batch
    /// when one is due. Returns how long to wait before the next iteration.
    /// </summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var queueRepo = scope.ServiceProvider.GetRequiredService<IUserEnrichmentQueueRepository>();
        var userCollector = scope.ServiceProvider.GetRequiredService<IUserCollector>();

        var now = _time.GetUtcNow();
        var pending = await queueRepo.CountPendingAsync(now.UtcDateTime, ct);

        if (pending >= _options.Phase4MinPendingThreshold)
        {
            if (_wasIdle)
            {
                _logger.LogInformation("Phase4Worker resuming (pending={Pending})", pending);
                _wasIdle = false;
            }
            _logger.LogDebug("Phase4Worker draining batch (pending={Pending})", pending);
            var batch = await userCollector.EnrichBatchAsync(ct);

            // Back off ONLY on a genuinely dead batch: nothing was pulled (queue
            // drained under a concurrent writer) or EVERY fetched row failed at the
            // network layer (Cloudflare 403/429 burst). A batch that enriched, parked
            // 404s, or deferred "n/a" rows IS real progress — loop straight into the
            // next batch instead of sleeping, so the queue actually drains.
            var deadBatch = batch.Processed == 0 || batch.Failed == batch.Processed;
            if (!deadBatch) return TimeSpan.Zero;

            _logger.LogWarning(
                "Phase4Worker made no progress (pending={Pending}, failed={Failed}/{Processed}); idling {Idle} to avoid hot loop",
                pending, batch.Failed, batch.Processed, _options.Phase4IdleInterval);
            _wasIdle = true;
            return _options.Phase4IdleInterval;
        }

        if (_options.ProfileRefreshPerHour > 0)
        {
            if (now >= _nextRefreshAt)
            {
                ScheduleNextRefresh(await userCollector.RefreshProfilesAsync(_refreshCursor, ct), now);
                return TimeSpan.Zero; // the queue first, then the refresh pace
            }
            var untilRefresh = _nextRefreshAt - now;
            if (untilRefresh < _options.Phase4IdleInterval) return untilRefresh;
        }

        LogIdle(pending);
        return _options.Phase4IdleInterval;
    }

    private void ScheduleNextRefresh(ProfileRefreshResult result, DateTimeOffset startedAt)
    {
        if (result.Processed > 0)
        {
            _refreshCursor = result.LastId;
            _settledInPass += result.Processed - result.Failed;
            _failedInPass += result.Failed;
            // The pace counts from the start of the batch, whatever the RSI gate made it wait.
            _nextRefreshAt = startedAt + TimeSpan.FromHours((double)result.Processed / _options.ProfileRefreshPerHour);
            return;
        }

        // Nothing left after the cursor: the pass is over. Another one at once only to
        // read again what failed, and only if this pass got somewhere.
        if (_failedInPass > 0 && _settledInPass > 0)
        {
            _logger.LogInformation(
                "Profile refresh pass over ({Settled} settled, {Failed} failed); reading the failed ones again",
                _settledInPass, _failedInPass);
            _nextRefreshAt = startedAt;
        }
        else
        {
            if (_failedInPass > 0)
            {
                _logger.LogWarning("Profile refresh pass over: all {Failed} reads failed; trying again in {Wait}",
                    _failedInPass, RefreshRecheck);
            }
            else
            {
                _logger.LogInformation("Profile refresh pass over ({Settled} settled): no profile left to read again; looking again in {Wait}",
                    _settledInPass, RefreshRecheck);
            }
            _nextRefreshAt = startedAt + RefreshRecheck;
        }
        _refreshCursor = 0;
        _settledInPass = 0;
        _failedInPass = 0;
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
