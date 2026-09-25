using System.Data.Common;
using Collector.Data;
using Collector.Data.Repositories;
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
/// A queue row is pending until it reaches a terminal outcome (enriched, gone,
/// abandoned). "n/a" profiles and transient failures wait for their next attempt.
/// </summary>
public sealed class EnrichmentQueueLifecycleTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TransactionCounter _transactions = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_transactions))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
    }

    private (UserEnrichmentQueueRepository Queue, TrackerDbContext Db) Create()
    {
        var db = _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();
        return (new UserEnrichmentQueueRepository(db), db);
    }

    private async Task<long> QueueAsync(string handle, int priority = 0, DateTime? queuedAt = null)
    {
        var (_, db) = Create();
        var row = new UserEnrichmentQueue { UserHandle = handle, Priority = priority, QueuedAt = queuedAt ?? Now.AddHours(-1) };
        db.UserEnrichmentQueue.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<UserEnrichmentQueue> RowAsync(long id)
    {
        var (_, db) = Create();
        return await db.UserEnrichmentQueue.AsNoTracking().SingleAsync(q => q.Id == id);
    }

    private async Task<List<string>> DueAsync(DateTime at)
    {
        var (queue, _) = Create();
        return (await queue.GetPendingAsync(100, at)).Select(q => q.UserHandle).ToList();
    }

    [Fact]
    public async Task Gone_IsTerminal_AndFreesTheHandle()
    {
        var id = await QueueAsync("gone404");

        await Create().Queue.MarkGoneAsync(id, "Gone (HTTP 404)", Now);

        var row = await RowAsync(id);
        row.Enriched.Should().BeTrue();
        row.Outcome.Should().Be(EnrichmentOutcome.Gone);
        row.EnrichedAt.Should().Be(Now);
        (await DueAsync(Now.AddYears(1))).Should().BeEmpty();
        (await Create().Queue.InsertPendingIgnoreDuplicatesAsync(
            [new UserEnrichmentQueue { UserHandle = "gone404", QueuedAt = Now }])).Should().Be(1);
    }

    [Fact]
    public async Task NoCitizenRecord_WaitsFourteenDays_WithoutSpendingAnAttempt()
    {
        var id = await QueueAsync("na");

        await Create().Queue.DeferAsync(id, "No citizen record (n/a)", Now);

        var row = await RowAsync(id);
        row.Outcome.Should().Be(EnrichmentOutcome.NoCitizenRecord);
        row.AttemptCount.Should().Be(0);
        row.Enriched.Should().BeFalse();
        (await DueAsync(Now.AddDays(14).AddSeconds(-1))).Should().BeEmpty();
        (await DueAsync(Now.AddDays(14))).Should().Equal("na");
    }

    [Fact]
    public async Task Failures_BackOff_ThenTheRowIsAbandoned()
    {
        var id = await QueueAsync("flaky");

        await Create().Queue.RecordFailureAsync(id, "throttled", maxAttempts: 3, Now);
        (await RowAsync(id)).NextAttemptAt.Should().Be(Now.AddHours(1));
        (await DueAsync(Now.AddMinutes(59))).Should().BeEmpty();

        await Create().Queue.RecordFailureAsync(id, "throttled", maxAttempts: 3, Now.AddHours(1));
        (await RowAsync(id)).NextAttemptAt.Should().Be(Now.AddHours(5));

        await Create().Queue.RecordFailureAsync(id, "throttled", maxAttempts: 3, Now.AddHours(5));
        var row = await RowAsync(id);
        row.Enriched.Should().BeTrue();
        row.Outcome.Should().Be(EnrichmentOutcome.Abandoned);
        row.AttemptCount.Should().Be(3);
    }

    [Fact]
    public async Task NewcomersComeFirst_ThenTheOldestRows()
    {
        await QueueAsync("old-orphan", priority: 0, queuedAt: Now.AddDays(-3));
        await QueueAsync("newcomer", priority: 1, queuedAt: Now.AddHours(-1));
        await QueueAsync("recent-orphan", priority: 0, queuedAt: Now.AddDays(-1));

        (await DueAsync(Now)).Should().Equal("newcomer", "old-orphan", "recent-orphan");
        (await Create().Queue.GetPendingAsync(2, Now)).Select(q => q.UserHandle).Should().Equal("newcomer", "old-orphan");
    }

    [Fact]
    public async Task Count_OnlyCountsRowsThatAreDue()
    {
        await QueueAsync("due");
        var waiting = await QueueAsync("waiting");
        await Create().Queue.DeferAsync(waiting, "No citizen record (n/a)", Now);

        (await Create().Queue.CountPendingAsync(Now)).Should().Be(1);
    }

    [Fact]
    public async Task RecentlySettledHandles_AreTheRecentlyGoneOrAbandoned()
    {
        var recent = await QueueAsync("recently-gone");
        var old = await QueueAsync("long-gone");
        var fine = await QueueAsync("enriched");
        await Create().Queue.MarkGoneAsync(recent, "Gone (HTTP 404)", Now.AddDays(-10));
        await Create().Queue.MarkGoneAsync(old, "Gone (HTTP 404)", Now.AddDays(-40));
        await Create().Queue.MarkEnrichedAsync(fine, Now.AddDays(-1));

        var settled = await Create().Queue.GetRecentlySettledHandlesInAsync(
            ["recently-gone", "long-gone", "enriched", "never-queued"], since: Now.AddDays(-30));

        settled.Should().BeEquivalentTo(["recently-gone"]);
    }

    [Fact]
    public async Task InsertingABatch_UsesOneTransaction()
    {
        _transactions.Started = 0;

        var inserted = await Create().Queue.InsertPendingIgnoreDuplicatesAsync(
            Enumerable.Range(0, 20).Select(i => new UserEnrichmentQueue { UserHandle = $"new-{i}", QueuedAt = Now }).ToList());

        inserted.Should().Be(20);
        _transactions.Started.Should().Be(1);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }

    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        public int Started;

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Started);
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }
    }
}
