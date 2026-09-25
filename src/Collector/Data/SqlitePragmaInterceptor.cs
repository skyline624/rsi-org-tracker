using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Collector.Data;

/// <summary>
/// Per-connection SQLite settings, applied each time EF opens a connection.
/// Shared by the collector and the API (both register TrackerDbContext through
/// AddCollectorDataServices). journal_mode=WAL is a property of the file, set
/// once at startup by EnsureDatabaseAsync.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        // NORMAL is safe in WAL mode (a power cut can lose the last commits, never
        // corrupt the file) and avoids an fsync on every commit.
        "PRAGMA synchronous = NORMAL;" +
        // Wait for the other process's write lock instead of failing at once.
        "PRAGMA busy_timeout = 5000;" +
        // 32 MiB page cache per connection (the default is 2 MiB).
        "PRAGMA cache_size = -32768;" +
        // Truncate the WAL back to 64 MiB after checkpoints instead of keeping its peak size.
        "PRAGMA journal_size_limit = 67108864;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
