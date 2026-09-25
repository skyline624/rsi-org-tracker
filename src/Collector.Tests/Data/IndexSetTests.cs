using Collector.Data;
using Collector.Extensions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// The indexes tracker.db must carry, created by migrations rather than raw SQL at
/// startup, and the ones dropped because no query uses them.
/// </summary>
public sealed class IndexSetTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
    }

    private async Task<List<string>> IndexesAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        return await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_%'").ToListAsync();
    }

    [Fact]
    public async Task QueriedIndexes_Exist()
    {
        (await IndexesAsync()).Should().Contain([
            "IX_organization_members_UserHandle_NoCase", // user search by handle prefix
            "IX_change_events_ChangeType_Timestamp",     // /changes summary (skip-scan over types)
            "IX_change_events_ChangeType",               // /changes by type, newest Id first
            "IX_discovered_organizations_DeadAt",
        ]);
    }

    [Fact]
    public async Task UnusedIndexes_AreGone()
    {
        (await IndexesAsync()).Should().NotContain([
            "IX_organizations_Sid",                  // prefix of the unique (Sid, Timestamp)
            "IX_member_collection_log_CitizenId",
            "IX_member_collection_log_UserHandle",
        ]);
    }

    [Fact]
    public async Task TheNoCaseIndex_ComesFromAMigration_NotFromStartupSql()
    {
        var bootstrap = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Collector", "Extensions", "ServiceCollectionExtensions.cs"));

        bootstrap.Should().NotContain("IX_organization_members_UserHandle_NoCase");
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
