using System.Text.Json;
using Collector.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

/// <summary>Where the database lives on disk, for the maintenance safety stops.</summary>
public interface IStorageProbe
{
    long GetWalBytes();

    long GetFreeDiskBytes();

    /// <summary>Called after each WAL checkpoint.</summary>
    void Checkpointed() { }
}

public sealed class FileStorageProbe : IStorageProbe
{
    private readonly string _databasePath;

    public FileStorageProbe(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
    }

    public long GetWalBytes()
    {
        var wal = new FileInfo(_databasePath + "-wal");
        return wal.Exists ? wal.Length : 0;
    }

    public long GetFreeDiskBytes()
        => new DriveInfo(OperatingSystem.IsWindows()
            ? Path.GetPathRoot(_databasePath)!
            : Path.GetDirectoryName(_databasePath)!).AvailableFreeSpace;
}

public sealed record PurgeReport(string Target, int Matched, int Deleted, int Batches, string? StoppedBecause);

/// <summary>
/// One-shot database maintenance (<c>Collector --maintenance ...</c>), run with the
/// collector service stopped. Purges delete rows in batches of their own
/// transaction, checkpoint the WAL as they go, and stop by themselves when the WAL
/// stays above 1 GiB or free disk falls under 3 GiB. No VACUUM: freed pages go to
/// the freelist, which the database reuses as it grows.
/// </summary>
public sealed class MaintenanceService
{
    public const long MaxWalBytes = 1L << 30;
    public const long MinFreeDiskBytes = 3L << 30;
    private const int CheckpointEveryBatches = 10;

    private sealed record Target(string Name, string Table, string Description, string SelectIds);

    private static readonly Target[] Targets =
    [
        new("content-null-events", "change_events",
            "page-text *_changed events with no old value: Phase 2 compared with a listing snapshot whose texts are NULL",
            """
            SELECT Id FROM change_events
            WHERE ChangeType IN ('description_changed', 'history_changed', 'manifesto_changed',
                                 'charter_changed', 'focus_primary_changed', 'focus_secondary_changed')
              AND OldValue IS NULL
            """),
        new("member-count-repeats", "change_events",
            "member_count_changed events repeating the org's previous value (cached listing count flip-flopping)",
            """
            SELECT Id FROM (
                SELECT Id, NewValue, LAG(NewValue) OVER (PARTITION BY OrgSid ORDER BY Id) AS Previous
                FROM change_events WHERE ChangeType = 'member_count_changed')
            WHERE NewValue = Previous
            """),
        new("queue-enriched", "user_enrichment_queue",
            "enrichment queue rows enriched more than 7 days ago",
            """
            SELECT Id FROM user_enrichment_queue
            WHERE Enriched = 1 AND (Outcome IS NULL OR Outcome = 'enriched') AND EnrichedAt < $cutoff7
            """),
        new("queue-terminal", "user_enrichment_queue",
            "enrichment queue rows gone or abandoned more than 90 days ago",
            """
            SELECT Id FROM user_enrichment_queue
            WHERE Enriched = 1 AND Outcome IN ('gone', 'abandoned') AND EnrichedAt < $cutoff90
            """),
    ];

    /// <summary>Counted by <see cref="MeasureAsync"/> but never purged: they are recognized, not proven wrong.</summary>
    private static readonly (string Label, string Sql)[] Measurements =
    [
        ("rank_changed with v1 overlay titles (Roles/Affiliate)",
            "SELECT COUNT(*) FROM change_events WHERE ChangeType = 'rank_changed' AND (OldValue IN ('Roles', 'Affiliate') OR NewValue IN ('Roles', 'Affiliate'))"),
        ("roles_changed (v1 parser)", "SELECT COUNT(*) FROM change_events WHERE ChangeType = 'roles_changed'"),
        ("member_left in bursts of 32+ per org and collection (truncated rosters)",
            "SELECT COALESCE(SUM(n), 0) FROM (SELECT COUNT(*) AS n FROM change_events WHERE ChangeType = 'member_left' GROUP BY OrgSid, Timestamp HAVING n >= 32)"),
        ("freelist pages", "SELECT freelist_count FROM pragma_freelist_count"),
    ];

    public static IReadOnlyList<string> TargetNames { get; } = Targets.Select(t => t.Name).ToList();

    private readonly TrackerDbContext _db;
    private readonly IStorageProbe _storage;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(TrackerDbContext db, IStorageProbe storage, ILogger<MaintenanceService> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    public async Task<PurgeReport> PurgeAsync(
        string targetName, bool dryRun, int batchSize, DateTime now, CancellationToken ct = default)
    {
        var target = Targets.FirstOrDefault(t => t.Name == targetName)
            ?? throw new ArgumentException(
                $"Unknown purge target '{targetName}'. Known targets: {string.Join(", ", TargetNames)}", nameof(targetName));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (SqliteConnection)_db.Database.GetDbConnection();

            // The matching ids are captured once: every batch then deletes by primary key.
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS temp.purge_ids; CREATE TEMP TABLE purge_ids (id INTEGER PRIMARY KEY);", now, ct);
            var matched = await ExecuteAsync(connection, $"INSERT INTO temp.purge_ids (id) {target.SelectIds};", now, ct);
            _logger.LogInformation("Purge {Target}: {Matched} rows match ({Description}){DryRun}",
                target.Name, matched, target.Description, dryRun ? " — dry run, nothing deleted" : "");
            if (dryRun) return new PurgeReport(target.Name, matched, 0, 0, null);

            long lastId = 0;
            int deleted = 0, batches = 0;
            string? stoppedBecause = null;
            while (true)
            {
                var freeBytes = _storage.GetFreeDiskBytes();
                if (freeBytes < MinFreeDiskBytes)
                {
                    stoppedBecause = $"free disk below 3 GiB ({freeBytes >> 20} MiB left)";
                    break;
                }

                var ids = await NextIdsAsync(connection, lastId, batchSize, ct);
                if (ids.Count == 0) break;

                await using (var transaction = await connection.BeginTransactionAsync(ct))
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = (SqliteTransaction)transaction;
                    delete.CommandText = $"DELETE FROM {target.Table} WHERE Id IN (SELECT value FROM json_each($ids));";
                    delete.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
                    deleted += await delete.ExecuteNonQueryAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                lastId = ids[^1];
                batches++;

                if (batches % CheckpointEveryBatches == 0 || _storage.GetWalBytes() > MaxWalBytes)
                {
                    await CheckpointAsync(connection, ct);
                    if (_storage.GetWalBytes() > MaxWalBytes)
                    {
                        stoppedBecause = "WAL still above 1 GiB after a checkpoint (a reader may be holding it)";
                        break;
                    }
                    _logger.LogInformation("Purge {Target}: {Deleted}/{Matched} deleted", target.Name, deleted, matched);
                }
            }

            await CheckpointAsync(connection, ct);
            if (stoppedBecause != null)
            {
                _logger.LogWarning("Purge {Target} stopped after {Deleted}/{Matched} rows: {Reason}",
                    target.Name, deleted, matched, stoppedBecause);
            }
            else
            {
                _logger.LogInformation("Purge {Target} done: {Deleted} rows deleted in {Batches} batches",
                    target.Name, deleted, batches);
            }
            return new PurgeReport(target.Name, matched, deleted, batches, stoppedBecause);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>What each purge would delete, and the anomalies that are only measured.</summary>
    public async Task<IReadOnlyList<(string Label, long Count)>> MeasureAsync(DateTime now, CancellationToken ct = default)
    {
        var results = new List<(string, long)>();
        foreach (var target in Targets)
        {
            results.Add(($"{target.Name}: {target.Description}", (await PurgeAsync(target.Name, dryRun: true, 1, now, ct)).Matched));
        }

        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (SqliteConnection)_db.Database.GetDbConnection();
            foreach (var (label, sql) in Measurements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                results.Add((label, Convert.ToInt64(await command.ExecuteScalarAsync(ct))));
            }
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }

        foreach (var (label, count) in results)
        {
            _logger.LogInformation("{Count,12:N0}  {Label}", count, label);
        }
        return results;
    }

    /// <summary>PRAGMA quick_check: "ok", or the first problems found.</summary>
    public async Task<string> QuickCheckAsync(CancellationToken ct = default)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA quick_check(20);";
            var rows = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0));
            }
            var result = string.Join("; ", rows);
            _logger.LogInformation("quick_check: {Result}", result);
            return result;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, DateTime now, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff7", now.AddDays(-7));
        command.Parameters.AddWithValue("$cutoff90", now.AddDays(-90));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<long>> NextIdsAsync(SqliteConnection connection, long after, int batchSize, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM temp.purge_ids WHERE id > $after ORDER BY id LIMIT $limit;";
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", batchSize);
        var ids = new List<long>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private async Task CheckpointAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(ct);
        _storage.Checkpointed();
    }
}
