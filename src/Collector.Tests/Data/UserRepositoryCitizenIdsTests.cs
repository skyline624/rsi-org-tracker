using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// UserRepository.GetCitizenIdsByHandlesAsync: UserHandle is not unique (handles are
/// reused, only CitizenId is unique), and the case-insensitive keys collapse case variants.
/// </summary>
public sealed class UserRepositoryCitizenIdsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TrackerDbContext _db;
    private readonly UserRepository _sut;

    public UserRepositoryCitizenIdsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _sut = new UserRepository(_db);
    }

    [Fact]
    public async Task ReusedHandle_TwoCitizens_TakesTheLatestOwner()
    {
        _db.Users.AddRange(
            new User { CitizenId = 1, UserHandle = "Harion", CreatedAt = new DateTime(2024, 1, 1), UpdatedAt = new DateTime(2024, 1, 1) },
            new User { CitizenId = 2, UserHandle = "Harion", CreatedAt = new DateTime(2026, 1, 1), UpdatedAt = new DateTime(2026, 6, 1) });
        await _db.SaveChangesAsync();

        var map = await _sut.GetCitizenIdsByHandlesAsync(["Harion"]);

        map["Harion"].Should().Be(2, "the most recently updated row wins");
    }

    [Fact]
    public async Task CaseVariantHandles_DoNotCollide()
    {
        _db.Users.AddRange(
            new User { CitizenId = 10, UserHandle = "Gallus", CreatedAt = DateTime.UtcNow, UpdatedAt = new DateTime(2026, 1, 1) },
            new User { CitizenId = 11, UserHandle = "gallus", CreatedAt = DateTime.UtcNow, UpdatedAt = new DateTime(2026, 5, 1) });
        await _db.SaveChangesAsync();

        var act = async () => await _sut.GetCitizenIdsByHandlesAsync(["Gallus", "gallus"]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CurrentHandlesComeFromUsers_FormerOnesFromTheHandleHistory()
    {
        _db.Users.Add(new User { CitizenId = 20, UserHandle = "Alice", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.UserHandleHistories.AddRange(
            new UserHandleHistory { CitizenId = 20, UserHandle = "OldAlice", FirstSeen = new DateTime(2025, 1, 1), LastSeen = new DateTime(2025, 6, 1) },
            new UserHandleHistory { CitizenId = 99, UserHandle = "Alice", FirstSeen = new DateTime(2020, 1, 1), LastSeen = new DateTime(2021, 1, 1) });
        await _db.SaveChangesAsync();

        var map = await _sut.GetCitizenIdsByHandlesAsync(["Alice", "OldAlice", "Ghost"]);

        map["Alice"].Should().Be(20, "the current owner of a handle wins over a former one");
        map["OldAlice"].Should().Be(20);
        map.Should().NotContainKey("Ghost");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
