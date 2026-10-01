namespace Collector.Api.Services.Discord;

/// <summary>
/// Serialises every write to the discord_* tables of tracker.db (spec § 9.2): ingestion, links
/// and rejections, erasures, and each retention batch. discord_accounts is shared by every
/// guild, so two concurrent syncs would otherwise insert the same account twice. The API runs
/// as a single instance and receives a few syncs an hour, so one process-wide semaphore costs
/// nothing. SQLite remains the only arbiter against the collector.
/// </summary>
public sealed class DiscordWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits at most <paramref name="timeout"/>; null when the gate stayed held.</summary>
    public async Task<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken ct) =>
        await _semaphore.WaitAsync(timeout, ct) ? new Lease(_semaphore) : null;

    /// <summary>Waits as long as needed, for background work that must not give up.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        return new Lease(_semaphore);
    }

    /// <summary>Releases the gate once, however many times it is disposed.</summary>
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
