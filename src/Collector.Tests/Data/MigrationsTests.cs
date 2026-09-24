using Collector.Data;
using Collector.Extensions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// Safety net for schema changes: the full migration chain plus the startup
/// bootstrap must build a working tracker.db from nothing.
/// </summary>
public sealed class MigrationsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public MigrationsTests()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
    }

    [Fact]
    public async Task EnsureDatabase_OnEmptyDatabase_AppliesEveryMigration()
    {
        await _provider.EnsureDatabaseAsync(Path.Combine(Path.GetTempPath(), "sc-tracker-tests"));

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Organizations.CountAsync()).Should().Be(0);
        (await db.DiscoveredOrganizations.CountAsync()).Should().Be(0);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
