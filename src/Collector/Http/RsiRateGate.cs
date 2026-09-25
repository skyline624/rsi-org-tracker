using Collector.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Collector.Http;

/// <summary>
/// Process-wide request budget for robertsspaceindustries.com. Every RsiApiClient
/// instance (one per DI scope, plus the Phase 4 worker running in parallel) goes
/// through this singleton, so the limits hold for the process, not per instance:
/// <list type="bullet">
/// <item>at most <see cref="CollectorOptions.MaxConcurrentRequests"/> requests in flight;</item>
/// <item>at least <see cref="CollectorOptions.RateLimitDelaySeconds"/> between two request starts;</item>
/// <item>a shared pause after a throttle (HTTP 403/429/503, ErrApiThrottled), doubling
/// on each new throttle episode up to <see cref="CollectorOptions.MaxThrottlePauseSeconds"/>
/// and reset by the next success.</item>
/// </list>
/// </summary>
public sealed class RsiRateGate
{
    private readonly TimeProvider _time;
    private readonly ILogger<RsiRateGate> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _spacing;
    private readonly TimeSpan _basePause;
    private readonly TimeSpan _maxPause;

    private readonly object _lock = new();
    private DateTimeOffset _nextStart = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private TimeSpan _lastPause = TimeSpan.Zero;

    public RsiRateGate(IOptions<CollectorOptions> options, TimeProvider time, ILogger<RsiRateGate> logger)
    {
        var o = options.Value;
        _time = time;
        _logger = logger;
        _slots = new SemaphoreSlim(o.MaxConcurrentRequests, o.MaxConcurrentRequests);
        _spacing = TimeSpan.FromSeconds(o.RateLimitDelaySeconds);
        _basePause = TimeSpan.FromSeconds(o.ThrottlePauseSeconds);
        _maxPause = TimeSpan.FromSeconds(o.MaxThrottlePauseSeconds);
    }

    /// <summary>Remaining shared pause (zero when requests may go out).</summary>
    public TimeSpan PausedFor
    {
        get
        {
            lock (_lock)
            {
                var left = _pausedUntil - _time.GetUtcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Waits for a concurrency slot, then for the spacing and any shared pause.
    /// Dispose the result once the response has been read.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = _time.GetUtcNow();
                    var start = Max(now, Max(_nextStart, _pausedUntil));
                    if (start <= now)
                    {
                        _nextStart = now + _spacing;
                        return new Lease(_slots);
                    }
                    wait = start - now;
                }
                // Re-checked after the wait: a pause may have started meanwhile.
                await Task.Delay(wait, _time, ct);
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>
    /// RSI or Cloudflare refused a request: every caller pauses. Refusals that arrive
    /// while a pause is running belong to the same episode and do not escalate it.
    /// </summary>
    public void ReportThrottled(TimeSpan? retryAfter = null)
    {
        TimeSpan pause;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (now < _pausedUntil) return;

            _lastPause = _lastPause == TimeSpan.Zero
                ? _basePause
                : Min(_lastPause + _lastPause, _maxPause);
            pause = retryAfter is { } asked && asked > _lastPause ? Min(asked, _maxPause) : _lastPause;
            _pausedUntil = now + pause;
        }
        _logger.LogWarning("RSI throttled the collector: all requests paused for {Pause}s", pause.TotalSeconds);
    }

    /// <summary>A request went through: the next throttle starts again from the base pause.</summary>
    public void ReportSuccess()
    {
        lock (_lock) _lastPause = TimeSpan.Zero;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }
}
