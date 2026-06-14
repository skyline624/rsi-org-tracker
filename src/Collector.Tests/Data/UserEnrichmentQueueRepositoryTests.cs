using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// Integration-style tests for <see cref="UserEnrichmentQueueRepository"/>. Covers the
/// post-audit fixes: C6 (IsQueued/AddRange race) and the partial-unique-index
/// guarantee that duplicates get silently dropped rather than tearing down the
/// surrounding transaction.
/// </summary>
public sealed class UserEnrichmentQueueRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TrackerDbContext _db;
    private readonly UserEnrichmentQueueRepository _sut;

    public UserEnrichmentQueueRepositoryTests()
    {
        // In-memory SQLite — stays open for the lifetime of the connection.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TrackerDbContext(options);
        _db.Database.EnsureCreated();

        // Partial unique index on UserHandle where Enriched=0 — the production code
        // creates it via raw SQL in ServiceCollectionExtensions.EnsureDatabaseAsync.
        _db.Database.ExecuteSqlRaw(@"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_user_enrichment_queue_UserHandle_Pending
            ON user_enrichment_queue (UserHandle)
            WHERE Enriched = 0;
        ");

        _sut = new UserEnrichmentQueueRepository(_db);
    }

    [Fact]
    public async Task InsertPendingIgnoreDuplicates_AllNew_InsertsEverything()
    {
        var items = new[]
        {
            new UserEnrichmentQueue { UserHandle = "alice", Priority = 1, QueuedAt = DateTime.UtcNow },
            new UserEnrichmentQueue { UserHandle = "bob",   Priority = 0, QueuedAt = DateTime.UtcNow },
        };

        var inserted = await _sut.InsertPendingIgnoreDuplicatesAsync(items);

        inserted.Should().Be(2);
        var all = await _db.UserEnrichmentQueue.CountAsync();
        all.Should().Be(2);
    }

    [Fact]
    public async Task InsertPendingIgnoreDuplicates_DuplicatePendingHandle_DropsDuplicateSilently()
    {
        // Arrange — pre-seed a pending row
        _db.UserEnrichmentQueue.Add(new UserEnrichmentQueue
        {
            UserHandle = "alice",
            Priority = 1,
            Enriched = false,
            QueuedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // Act — try to reinsert the same handle + a fresh one
        var batch = new[]
        {
            new UserEnrichmentQueue { UserHandle = "alice", Priority = 0, QueuedAt = DateTime.UtcNow },
            new UserEnrichmentQueue { UserHandle = "bob",   Priority = 1, QueuedAt = DateTime.UtcNow },
        };

        var inserted = await _sut.InsertPendingIgnoreDuplicatesAsync(batch);

        // Assert — only the fresh handle was written. No exception, no tear-down.
        inserted.Should().Be(1);
        (await _db.UserEnrichmentQueue.CountAsync()).Should().Be(2);
        (await _db.UserEnrichmentQueue.CountAsync(q => q.UserHandle == "alice"))
            .Should().Be(1, "the partial unique index blocks the duplicate pending row");
    }

    [Fact]
    public async Task InsertPendingIgnoreDuplicates_HandleAlreadyEnriched_ReinsertAllowed()
    {
        // Arrange — a previously-enriched row must NOT block a new pending one,
        // because the partial unique index excludes Enriched=1.
        _db.UserEnrichmentQueue.Add(new UserEnrichmentQueue
        {
            UserHandle = "alice",
            Priority = 1,
            Enriched = true,
            EnrichedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow.AddDays(-1),
        });
        await _db.SaveChangesAsync();

        var batch = new[]
        {
            new UserEnrichmentQueue { UserHandle = "alice", Priority = 0, QueuedAt = DateTime.UtcNow },
        };

        var inserted = await _sut.InsertPendingIgnoreDuplicatesAsync(batch);

        inserted.Should().Be(1);
        (await _db.UserEnrichmentQueue.CountAsync(q => q.UserHandle == "alice"))
            .Should().Be(2);
    }

    [Fact]
    public async Task MarkGone_ParksRowForeverAndDoesNotEnrich()
    {
        _db.UserEnrichmentQueue.Add(new UserEnrichmentQueue
        {
            UserHandle = "gone404", Priority = 0, Enriched = false, QueuedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        var id = (await _db.UserEnrichmentQueue.SingleAsync()).Id;

        await _sut.MarkGoneAsync(id, "Gone (HTTP 404)");

        // Excluded from the worker's pending set (AttemptCount < maxAttempts is false)…
        (await _sut.GetPendingAsync(100, maxAttempts: 3)).Should().BeEmpty();
        (await _sut.CountPendingAsync(3)).Should().Be(0);
        // …but it is NOT marked enriched.
        var row = await _db.UserEnrichmentQueue.FindAsync(id);
        row!.Enriched.Should().BeFalse();
        row.LastError.Should().Be("Gone (HTTP 404)");
    }

    [Fact]
    public async Task Defer_MovesToBackOfQueueWithoutSpendingAnAttempt()
    {
        // A (older) then B (newer) — both pending, same priority.
        _db.UserEnrichmentQueue.AddRange(
            new UserEnrichmentQueue { UserHandle = "A_na", Priority = 0, QueuedAt = new DateTime(2026, 1, 1) },
            new UserEnrichmentQueue { UserHandle = "B_real", Priority = 0, QueuedAt = new DateTime(2026, 1, 2) });
        await _db.SaveChangesAsync();
        var a = await _db.UserEnrichmentQueue.SingleAsync(q => q.UserHandle == "A_na");

        // Before: A is first (oldest QueuedAt).
        (await _sut.GetPendingAsync(1, 3)).Single().UserHandle.Should().Be("A_na");

        await _sut.DeferAsync(a.Id, "No citizen record (n/a)");

        // After: A moved to the back → B now surfaces first, A is still eligible (last).
        var ordered = await _sut.GetPendingAsync(10, 3);
        ordered.Select(q => q.UserHandle).Should().Equal("B_real", "A_na");

        var reloaded = await _db.UserEnrichmentQueue.FindAsync(a.Id);
        reloaded!.AttemptCount.Should().Be(0, "deferring n/a must never count towards the abandon cap");
        reloaded.Enriched.Should().BeFalse();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
