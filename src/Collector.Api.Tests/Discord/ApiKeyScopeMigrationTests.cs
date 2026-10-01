using Collector.Api.Data;
using Collector.Api.Models;
using Collector.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>Legacy adoption must not claim that an absent API-key scope column was migrated.</summary>
public sealed class ApiKeyScopeMigrationTests
{
    [Fact]
    public async Task LegacyDatabaseWithoutHistory_AppliesScopeMigration_AndPreservesFullKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.GetService<IMigrator>().MigrateAsync("20260925114422_AddLoginLockout");
        db.ApiUsers.Add(new ApiUser
        {
            Id = 17, Username = "legacy", Email = "legacy@example.test", PasswordHash = "unused-test-hash",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO api_keys (ApiUserId, Name, KeyHash, KeyPrefix, CreatedAt, IsRevoked)
            VALUES (17, 'legacy full key', 'test-hash', 'prefix', '2026-09-01 00:00:00', 0);
            DROP TABLE "__EFMigrationsHistory";
            """);

        await DatabaseBootstrap.MigrateOrAdoptAsync(db, "api_users");
        await DatabaseBootstrap.MigrateOrAdoptAsync(db, "api_users");

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(id => id.EndsWith("_AddApiKeyScope"));
        var key = await db.ApiKeys.SingleAsync();
        key.Name.Should().Be("legacy full key");
        key.Scope.Should().BeNull("existing keys retain full access after the additive migration");
    }

    [Fact]
    public async Task EnsureCreatedCurrentSchema_IsAdopted_WithoutAddingScopeTwice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        await DatabaseBootstrap.MigrateOrAdoptAsync(db, "api_users");

        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.ApiKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task IncompatibleLegacySchema_IsRefused_WithoutWritingMigrationHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE api_users (Id INTEGER PRIMARY KEY)");

        var adopt = () => DatabaseBootstrap.MigrateOrAdoptAsync(db, "api_users");

        await adopt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Refusing*");
        var historyTables = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'")
            .SingleAsync();
        historyTables.Should().Be(0);
    }

    private static ApiDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options);
}
