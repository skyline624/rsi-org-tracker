using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Collector.Tests.TestSupport;

/// <summary>
/// Counts the rows each EF transaction writes, from SQLite's total_changes() when it starts and
/// just before it commits: inserts, updates and deletes alike, ExecuteUpdate/ExecuteDelete
/// included. Proves that bulk writes stay within their per-transaction bound. Only the async
/// path is intercepted, the one the Discord repositories use.
/// </summary>
public sealed class SqliteRowsPerTransaction : DbTransactionInterceptor
{
    private long _atStart;

    /// <summary>Rows written by each committed transaction, in commit order.</summary>
    public List<long> Rows { get; } = [];

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        _atStart = await TotalChangesAsync(connection, result, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Rows.Add(await TotalChangesAsync(transaction.Connection!, transaction, cancellationToken) - _atStart);
        return result;
    }

    private static async Task<long> TotalChangesAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT total_changes();";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
