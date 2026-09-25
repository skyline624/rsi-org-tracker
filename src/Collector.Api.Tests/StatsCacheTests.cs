using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Api.Services;
using Collector.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>Statistics scan the large tables (0.5-1.4 s on production data): they are cached a few minutes.</summary>
public sealed class StatsCacheTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
        await _db.Database.MigrateAsync();
    }

    [Fact]
    public async Task ASecondRequest_IsServedFromTheCache()
    {
        var first = await new StatsService(_db, _cache).GetOverviewAsync();
        _sql.Commands.Clear();

        var second = await new StatsService(_db, _cache).GetOverviewAsync();

        second.Should().BeEquivalentTo(first);
        _sql.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DifferentParameters_AreCachedApart()
    {
        await new StatsService(_db, _cache).GetTimelineAsync(7);
        _sql.Commands.Clear();

        await new StatsService(_db, _cache).GetTimelineAsync(30);

        _sql.Commands.Should().NotBeEmpty();
    }

    public async Task DisposeAsync()
    {
        _cache.Dispose();
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
