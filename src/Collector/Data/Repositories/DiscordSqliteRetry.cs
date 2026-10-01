using Collector.Discord;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collector.Data.Repositories;

/// <summary>
/// Runs one tracker.db write transaction for the Discord repositories (roster and erasure).
/// The collector shares the file and never retries, so a Discord write waits a bounded time
/// for the write lock, retries once after <see cref="RetryDelay"/> when SQLite is still busy,
/// then gives up with <see cref="DiscordStoreBusyException"/> (the API answers 503).
/// </summary>
internal static class DiscordSqliteRetry
{
    /// <summary>SQLITE_BUSY: another connection holds the write lock.</summary>
    public const int SqliteBusy = 5;

    /// <summary>Same wait as the connections' busy_timeout (SqlitePragmaInterceptor).</summary>
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Pause before the one retry.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction (BEGIN IMMEDIATE, so a busy database fails
    /// at the start, before anything is written). The context's change tracker is cleared after
    /// each attempt: the Discord repositories own the context while they write.
    /// </summary>
    public static async Task RunAsync(TrackerDbContext db, Func<Task> work, TimeSpan busyTimeout,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await InTransactionAsync(db, work, busyTimeout, ct);
                return;
            }
            catch (Exception e) when (IsBusy(e))
            {
                if (attempt == 2)
                {
                    throw new DiscordStoreBusyException("tracker.db is still locked by another writer.", e);
                }
                await delay(RetryDelay, ct);
            }
        }
    }

    private static async Task InTransactionAsync(TrackerDbContext db, Func<Task> work, TimeSpan busyTimeout, CancellationToken ct)
    {
        // Microsoft.Data.Sqlite retries a busy statement (BEGIN IMMEDIATE included) until the
        // command timeout, 30 s by default: bound it so a lock held by the collector surfaces
        // as busy after busyTimeout rather than after half a minute.
        var seconds = Math.Max(1, (int)Math.Ceiling(busyTimeout.TotalSeconds));
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var previousDefault = connection.DefaultTimeout;
        var previousCommand = db.Database.GetCommandTimeout();
        connection.DefaultTimeout = seconds;
        db.Database.SetCommandTimeout(seconds);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await work();
            await transaction.CommitAsync(ct);
        }
        finally
        {
            // Nothing half-written stays tracked: the retry, or the next batch, starts clean.
            db.ChangeTracker.Clear();
            connection.DefaultTimeout = previousDefault;
            db.Database.SetCommandTimeout(previousCommand);
        }
    }

    private static bool IsBusy(Exception e) =>
        e is SqliteException { SqliteErrorCode: SqliteBusy }
        || e.InnerException is SqliteException { SqliteErrorCode: SqliteBusy };
}
