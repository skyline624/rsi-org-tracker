using System.Security.Cryptography;
using System.Text;
using Collector.Api.Data;
using Collector.Api.Dtos.ApiKeys;
using Collector.Api.Models;
using Collector.Api.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>Upgrading api.db keeps existing credentials unrestricted and supports scoped credentials without model drift.</summary>
[Collection(ApiCollection.Name)]
public class ApiKeyScopeUpgradeTests
{
    [Fact]
    public async Task ScopeMigration_PreservesAFullKey_AndStoresANewIngestKey()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260925114422_AddLoginLockout");
        const string legacyKey = "legacy-prefix_unchanged-secret";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyKey))).ToLowerInvariant();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO api_users (Id, Username, Email, PasswordHash, CreatedAt, UpdatedAt, IsAdmin, IsBanned, FailedLoginCount)
            VALUES (1, {"legacy-owner"}, {"legacy@example.test"}, {"unused-password-hash"}, {now}, {now}, 0, 0, 0)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO api_keys (Id, ApiUserId, Name, KeyHash, KeyPrefix, CreatedAt, IsRevoked)
            VALUES (1, 1, {"legacy"}, {hash}, {"legacy"}, {now}, 0)
            """);

        await db.Database.MigrateAsync();

        var service = new ApiKeyService(db);
        var legacy = await service.ValidateAsync(legacyKey);
        legacy.Should().NotBeNull();
        legacy!.User.Username.Should().Be("legacy-owner");
        legacy.Scope.Should().BeNull();
        var (rawKey, _) = await service.CreateAsync(1,
            new CreateApiKeyRequest("ingest", DateTime.UtcNow.AddDays(30), ApiKeyScopes.DiscordIngest));
        db.ChangeTracker.Clear();
        (await service.ValidateAsync(rawKey))!.Scope.Should().Be(ApiKeyScopes.DiscordIngest);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
