using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// Org SIDs are stored in upper case, so lookups compare them as they are (and can use
/// the OrgSid indexes) instead of lower-casing both sides.
/// </summary>
public sealed class OrgSidCaseTests : IDisposable
{
    private const string Before = "20260925152503_IndexCleanup";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private readonly TrackerDbContext _db;

    public OrgSidCaseTests()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
    }

    [Fact]
    public async Task LegacyLowerCaseSids_AreUpperCasedByTheMigration()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO tracked_entities (Source, Status, CreatedAt, UpdatedAt) VALUES ('manual', 'active', '2026-01-01', '2026-01-01');
            INSERT INTO org_notes (OrgSid, AuthorApiUserId, AuthorUsername, Body, CreatedAt, UpdatedAt)
              VALUES ('abc', 1, 'someone', 'note', '2026-01-01', '2026-01-01');
            INSERT INTO entity_memberships (TrackedEntityId, OrgSid, Via, SinceDate, AuthorApiUserId, AuthorUsername, CreatedAt)
              VALUES (1, 'abc', 'discord', '2026-01-01', 1, 'someone', '2026-01-01');
            """);

        await migrator.MigrateAsync();

        (await _db.OrgNotes.Select(n => n.OrgSid).SingleAsync()).Should().Be("ABC");
        (await _db.EntityMemberships.Select(m => m.OrgSid).SingleAsync()).Should().Be("ABC");
    }

    [Fact]
    public async Task Lookups_NormalizeTheRequestedSid_WithoutLowerCasingTheColumn()
    {
        await _db.Database.MigrateAsync();
        _db.TrackedEntities.Add(new TrackedEntity { CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _db.OrgNotes.Add(new OrgNote { OrgSid = "ABC", AuthorUsername = "someone", Body = "note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.EntityMemberships.Add(new EntityMembership { TrackedEntityId = 1, OrgSid = "ABC", AuthorUsername = "someone", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _sql.Commands.Clear();

        (await new OrgNoteRepository(_db).GetByOrgSidAsync("abc")).Should().ContainSingle();
        (await new EntityMembershipRepository(_db).GetByOrgSidAsync("abc")).Should().ContainSingle();
        (await new EntityMembershipRepository(_db).GetByEntityAndOrgAsync(1, "abc")).Should().NotBeNull();

        _sql.Commands.Should().NotContain(c => c.Contains("lower(", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _db.Dispose();
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
