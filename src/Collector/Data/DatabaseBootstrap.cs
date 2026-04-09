using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
///      the database was built by EnsureCreated. We create the history table and fake-apply
///      every compiled migration so subsequent <c>MigrateAsync</c> calls become a no-op.
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
                "Adopting existing schema as baseline migration(s).");
            await BaselineAsync(db, ct);
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

    private static async Task BaselineAsync(DbContext db, CancellationToken ct)
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
            await using (var create = conn.CreateCommand())
            {
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                        ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                        ""ProductVersion"" TEXT NOT NULL
                    );";
                await create.ExecuteNonQueryAsync(ct);
            }

            foreach (var id in migrations)
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = @"
                    INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ($id, $ver);";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$ver", productVersion);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }
}
