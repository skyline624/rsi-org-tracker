using Collector.Data;
using Collector.Extensions;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>Connection settings shared by the collector and the API (both use AddCollectorDataServices).</summary>
public sealed class SqlitePragmaTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sct-pragma-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCollectorDataServices(new ConfigurationBuilder().Build(), _dataDir);
        _provider = services.BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(_dataDir);
    }

    private async Task<string> PragmaAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        return Convert.ToString(await cmd.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("synchronous", "1")]              // NORMAL: safe in WAL mode, far fewer fsyncs than FULL
    [InlineData("busy_timeout", "5000")]
    [InlineData("cache_size", "-32768")]          // 32 MiB page cache per connection
    [InlineData("journal_size_limit", "67108864")] // WAL truncated back to 64 MiB after checkpoints
    public async Task EveryConnection_GetsThePragmas(string pragma, string expected)
    {
        (await PragmaAsync(pragma)).Should().Be(expected);
    }

    [Fact]
    public async Task EnsureDatabase_PutsTheFileInWalMode()
    {
        (await PragmaAsync("journal_mode")).Should().Be("wal");
    }

    [Fact]
    public async Task Maintenance_RunsPragmaOptimize_OncePerDay()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T00:00:00Z"));
        var logger = new ListLogger<SqliteMaintenanceService>();
        using var service = new SqliteMaintenanceService(_provider.GetRequiredService<IServiceScopeFactory>(), time, logger);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(23));
        await Task.Delay(200);
        logger.Messages.Should().NotContain(m => m.Contains("optimize"));

        time.Advance(TimeSpan.FromHours(1));
        await WaitUntil(() => logger.Messages.Any(m => m.Contains("optimize")));
        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
        condition().Should().BeTrue();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
