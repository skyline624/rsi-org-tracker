using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace Collector.Data;

/// <summary>
/// Bootstraps a <see cref="DbContext"/> backed by SQLite. Replaces the previous
/// <c>EnsureCreatedAsync</c> workflow with a proper EF Core migration pipeline,
/// while gracefully adopting pre-existing databases that were originally created
/// by <c>EnsureCreated</c>.
///
/// Strategy:
///   1. If <c>__EFMigrationsHistory</c> is already present → <c>MigrateAsync</c> normally.
///   2. If not, but the sentinel table (e.g. <c>organizations</c>, <c>api_users</c>) exists,
///      the database was built by EnsureCreated. We baseline only through the newest
///      migration whose tables and columns are already present, then apply newer migrations.
///   3. If neither exists → fresh database, let <c>MigrateAsync</c> create the schema.
/// </summary>
public static class DatabaseBootstrap
{
    public static async Task MigrateOrAdoptAsync(
        DbContext db,
        string sentinelTable,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var hasHistory = await TableExistsAsync(db, "__EFMigrationsHistory", ct);
        var hasSentinel = await TableExistsAsync(db, sentinelTable, ct);

        if (!hasHistory && hasSentinel)
        {
            logger?.LogWarning(
                "Database already contains tables but no __EFMigrationsHistory. " +
                "Adopting the existing tables and columns as baseline migration(s).");
            await BaselineAsync(db, logger, ct);
        }

        await db.Database.MigrateAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string table, CancellationToken ct)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name";
            cmd.Parameters.AddWithValue("$name", table);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is string;
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }

    private static async Task BaselineAsync(DbContext db, ILogger? logger, CancellationToken ct)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var migrations = assembly.Migrations.Keys.OrderBy(k => k).ToList();
        if (migrations.Count == 0) return;

        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "8.0.0";

        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            var schema = await ReadSchemaAsync(conn, ct);
            string? baseline = null;
            foreach (var id in migrations.AsEnumerable().Reverse())
            {
                var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
                if (!ContainsModel(schema, migration.TargetModel)) continue;
                baseline = id;
                break;
            }

            if (baseline is null)
                throw new InvalidOperationException(
                    "The existing database does not match any migration's tables and columns. " +
                    "Refusing to mark missing schema changes as applied.");

            logger?.LogInformation("Adopting existing schema through migration {Migration}", baseline);
            // History must be complete or absent if adoption is interrupted.
            using var transaction = conn.BeginTransaction();
            await using (var create = conn.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                        ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                        ""ProductVersion"" TEXT NOT NULL
                    );";
                await create.ExecuteNonQueryAsync(ct);
            }

            foreach (var id in migrations.TakeWhile(id => string.CompareOrdinal(id, baseline) <= 0))
            {
                // EnsureCreated cannot express raw NOCASE index columns. Replay only explicitly
                // idempotent index creation before recording such a migration as applied.
                var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
                foreach (var operation in migration.UpOperations.OfType<SqlOperation>()
                             .Where(o => o.Sql.TrimStart().StartsWith("CREATE INDEX IF NOT EXISTS", StringComparison.OrdinalIgnoreCase)))
                {
                    await using var index = conn.CreateCommand();
                    index.Transaction = transaction;
                    index.CommandText = operation.Sql;
                    await index.ExecuteNonQueryAsync(ct);
                }
                await using var insert = conn.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"
                    INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ($id, $ver);";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$ver", productVersion);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }

    /// <summary>Reads metadata only; adopting a large tracker database never scans its rows.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> ReadSchemaAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var list = conn.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var query = conn.CreateCommand();
            query.CommandText = "SELECT name FROM pragma_table_info($table)";
            query.Parameters.AddWithValue("$table", table);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));
            schema.Add(table, columns);
        }
        return schema;
    }

    /// <summary>
    /// Checks the structural baseline. Later additive migrations remain pending when any of
    /// their tables or columns are absent; data-only migrations retain the legacy adoption rule.
    /// </summary>
    private static bool ContainsModel(Dictionary<string, HashSet<string>> schema, IModel model)
    {
        var hasTables = false;
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;
            hasTables = true;
            if (!schema.TryGetValue(table, out var columns)) return false;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(store);
                if (column is not null && !columns.Contains(column)) return false;
            }
        }
        return hasTables;
    }
}
