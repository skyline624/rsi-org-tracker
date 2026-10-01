using Collector.Api.Options;
using Collector.Data.Repositories;
using Microsoft.Extensions.Options;

namespace Collector.Api.Services.Discord;

/// <summary>What one retention pass deleted.</summary>
public sealed record DiscordRetentionResult(int SyncLogsDeleted, int AccountsPurged);

/// <summary>
/// Applies the Discord retention rules once a day (spec § 13.3): sync log rows older than
/// <c>Discord:Retention:SyncLogDays</c>, and unlinked accounts gone for longer than
/// <c>Discord:Retention:DepartedAccountDays</c>. It works in batches of <see cref="BatchSize"/>
/// and takes the Discord write gate again for each one, so an ingestion never waits for a
/// whole pass. The first pass comes <see cref="StartupDelay"/> after start; the next ones
/// every <see cref="Interval"/>, anchored to that first pass.
/// </summary>
public sealed class DiscordRetentionService(
    IServiceScopeFactory scopes,
    DiscordWriteGate gate,
    IOptions<DiscordOptions> options,
    TimeProvider time,
    ILogger<DiscordRetentionService> logger) : BackgroundService
{
    public const int DefaultBatchSize = 500;
    private DateTimeOffset? _firstRun;

    /// <summary>Wait before the first pass, so startup and the first ingestions go first.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Receipts or candidate accounts per gate lease; child writes have their own row bound.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>Anchor the schedule at host startup, before the background worker is dispatched.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _firstRun = time.GetUtcNow() + StartupDelay;
        return base.StartAsync(cancellationToken);
    }

    /// <summary>One pass: sync logs first, then departed accounts, each until a batch comes back short.</summary>
    public async Task<DiscordRetentionResult> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var retention = options.Value.Retention;
        // At least a day: a zero or negative setting must never purge what was just received.
        var syncCutoff = now.AddDays(-Math.Max(1, retention.SyncLogDays));
        var accountCutoff = now.AddDays(-Math.Max(1, retention.DepartedAccountDays));

        var syncLogs = await PurgeInBatchesAsync(
            (repo, c) => repo.PurgeSyncLogsAsync(syncCutoff, BatchSize, c), ct);
        var accounts = await PurgeInBatchesAsync(
            (repo, c) => repo.PurgeDepartedAccountsAsync(accountCutoff, BatchSize, c), ct);
        return new DiscordRetentionResult(syncLogs, accounts);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = _firstRun ?? time.GetUtcNow() + StartupDelay;
        try
        {
            while (true)
            {
                var wait = next - time.GetUtcNow();
                logger.LogDebug("Discord retention waiting {Delay} until {Next}", wait, next);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken);
                await RunSafelyAsync(stoppingToken);

                next += Interval;
                var now = time.GetUtcNow();
                if (next <= now) next = now + Interval;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunOnceAsync(ct);
            if (result.SyncLogsDeleted > 0 || result.AccountsPurged > 0)
                logger.LogInformation(
                    "Discord retention: deleted {SyncLogs} sync log rows, purged {Accounts} departed accounts",
                    result.SyncLogsDeleted, result.AccountsPurged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Discord retention pass failed; next attempt in {Interval}", Interval);
        }
    }

    /// <summary>
    /// Runs batches, each in a fresh scope and under the write gate, until one deletes fewer
    /// than <see cref="BatchSize"/> rows. Every batch deletes what it counts, so the loop ends.
    /// </summary>
    private async Task<int> PurgeInBatchesAsync(
        Func<IDiscordRetentionRepository, CancellationToken, Task<int>> purgeBatch, CancellationToken ct)
    {
        var total = 0;
        int deleted;
        do
        {
            using (await gate.EnterAsync(ct))
            {
                await using var scope = scopes.CreateAsyncScope();
                deleted = await purgeBatch(scope.ServiceProvider.GetRequiredService<IDiscordRetentionRepository>(), ct);
            }
            total += deleted;
        }
        while (deleted >= BatchSize);
        return total;
    }
}
