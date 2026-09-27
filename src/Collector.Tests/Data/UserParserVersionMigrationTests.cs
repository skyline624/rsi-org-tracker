using Collector.Data;
using Collector.Data.Repositories;
using Collector.Parsers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>The AddUserParserVersion migration on citizens stored by the previous code.</summary>
public sealed class UserParserVersionMigrationTests : IDisposable
{
    private const string Before = "20260925160501_UppercaseOrgSids";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TrackerDbContext _db;

    public UserParserVersionMigrationTests()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
    }

    [Fact]
    public async Task EveryStoredCitizen_IsToBeReadAgain()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users (CitizenId, UserHandle, DisplayName, CreatedAt, UpdatedAt) VALUES
              (100, 'no-name', NULL, '2025-11-20 00:00:00', '2025-11-20 00:00:00'),
              (200, 'named', 'Named', '2026-08-01 00:00:00', '2026-08-01 00:00:00');
            """);

        await migrator.MigrateAsync();

        (await new UserRepository(_db).GetProfilesToRefreshAsync(0, UserProfileHtmlParser.Version, 10))
            .Select(p => p.CitizenId).Should().Equal(100, 200);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
