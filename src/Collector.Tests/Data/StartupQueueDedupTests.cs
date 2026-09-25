using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Data;
using Collector.Extensions;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// The pending-queue deduplication used to scan user_enrichment_queue on every
/// start, although the partial unique index makes duplicates impossible.
/// </summary>
public sealed class StartupQueueDedupTests : IAsyncLifetime
{
    private const string PendingIndex = "IX_user_enrichment_queue_UserHandle_Pending";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_sql))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    [Fact]
    public async Task WithTheUniqueIndex_StartupDoesNotScanTheQueue()
    {
        _sql.Commands.Clear();

        await _provider.EnsureDatabaseAsync(Path.GetTempPath());

        _sql.Commands.Should().NotContain(c => c.Contains("DELETE FROM user_enrichment_queue"));
    }

    [Fact]
    public async Task LegacyDatabaseWithoutTheIndex_IsDeduplicatedThenIndexed()
    {
        var db = NewDb();
        await db.Database.ExecuteSqlRawAsync($"DROP INDEX {PendingIndex};");
        var queuedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        db.UserEnrichmentQueue.AddRange(
            new UserEnrichmentQueue { UserHandle = "dup", QueuedAt = queuedAt },
            new UserEnrichmentQueue { UserHandle = "dup", QueuedAt = queuedAt.AddHours(1) });
        await db.SaveChangesAsync();

        await _provider.EnsureDatabaseAsync(Path.GetTempPath());

        var check = NewDb();
        (await check.UserEnrichmentQueue.Where(q => q.UserHandle == "dup").Select(q => q.QueuedAt).ToListAsync())
            .Should().Equal(queuedAt);
        (await check.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}", PendingIndex)
            .SingleAsync()).Should().Be(1);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
