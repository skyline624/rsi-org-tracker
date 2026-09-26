using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Tests.Data;

public sealed class OrgMemberCountRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>Fails the first save that inserts counters (a locked database, a full disk).</summary>
    private sealed class FailFirstCountersSave : SaveChangesInterceptor
    {
        private bool _failed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_failed && eventData.Context!.ChangeTracker.Entries<OrgMemberCount>().Any())
            {
                _failed = true;
                throw new DbUpdateException("database is locked");
            }
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task AFailedWrite_LeavesNothingTracked_SoTheRosterTransactionDoesNotInsertItAgain()
    {
        // Deferred from the final review: the counts row stayed tracked after a failed save,
        // and MemberCollector's roster transaction then inserted it (and could fail on it).
        _connection.Open();
        var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(new FailFirstCountersSave()).Options);
        await db.Database.MigrateAsync();

        var act = () => new OrgMemberCountRepository(db).RecordIfChangedAsync(new OrgMemberCount
        {
            OrgSid = "ORG", CollectedAt = DateTime.UtcNow, TotalRows = 3,
        });

        await act.Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Entries<OrgMemberCount>().Should().BeEmpty();
    }

    public void Dispose() => _connection.Dispose();
}
