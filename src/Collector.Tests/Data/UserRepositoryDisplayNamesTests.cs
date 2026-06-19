using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// Guards UserRepository.GetDisplayNamesByHandlesAsync against the duplicate-key
/// crash that aborted member collection for any org containing a reused handle
/// (e.g. "Harion", "Gallus"): a handle can map to several users rows because
/// UserHandle is not unique (only CitizenId is), and the OrdinalIgnoreCase key
/// also collapses case variants.
/// </summary>
public sealed class UserRepositoryDisplayNamesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TrackerDbContext _db;
    private readonly UserRepository _sut;

    public UserRepositoryDisplayNamesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _sut = new UserRepository(_db);
    }

    [Fact]
    public async Task ReusedHandle_TwoCitizens_DoesNotThrow_AndTakesLatest()
    {
        // Same handle "Harion" held by two different citizens — picks the newest row.
        _db.Users.AddRange(
            new User { CitizenId = 1, UserHandle = "Harion", DisplayName = "old owner",
                       CreatedAt = new DateTime(2024, 1, 1), UpdatedAt = new DateTime(2024, 1, 1) },
            new User { CitizenId = 2, UserHandle = "Harion", DisplayName = "new owner",
                       CreatedAt = new DateTime(2026, 1, 1), UpdatedAt = new DateTime(2026, 6, 1) });
        await _db.SaveChangesAsync();

        var map = await _sut.GetDisplayNamesByHandlesAsync(new[] { "Harion" });

        map.Should().ContainKey("Harion");
        map["Harion"].Should().Be("new owner", "the most recently updated row wins");
    }

    [Fact]
    public async Task CaseVariantHandles_DoNotCollide()
    {
        _db.Users.AddRange(
            new User { CitizenId = 10, UserHandle = "Gallus", DisplayName = "A",
                       CreatedAt = DateTime.UtcNow, UpdatedAt = new DateTime(2026, 1, 1) },
            new User { CitizenId = 11, UserHandle = "gallus", DisplayName = "B",
                       CreatedAt = DateTime.UtcNow, UpdatedAt = new DateTime(2026, 5, 1) });
        await _db.SaveChangesAsync();

        // Must not throw despite "Gallus"/"gallus" mapping to the same case-insensitive key.
        var act = async () => await _sut.GetDisplayNamesByHandlesAsync(new[] { "Gallus", "gallus" });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NormalHandles_StillResolve()
    {
        _db.Users.AddRange(
            new User { CitizenId = 20, UserHandle = "Alice", DisplayName = "Alice A.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new User { CitizenId = 21, UserHandle = "Bob", DisplayName = null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var map = await _sut.GetDisplayNamesByHandlesAsync(new[] { "Alice", "Bob", "Ghost" });

        map["Alice"].Should().Be("Alice A.");
        map["Bob"].Should().BeNull();
        map.Should().NotContainKey("Ghost");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
