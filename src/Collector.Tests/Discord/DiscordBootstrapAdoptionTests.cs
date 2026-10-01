using Collector.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>Adopting an EnsureCreated database also creates indexes absent from the EF model.</summary>
public sealed class DiscordBootstrapAdoptionTests
{
    [Fact]
    public async Task CurrentEnsureCreatedSchema_GetsTheRawHandleIndexesDuringAdoption()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        await DatabaseBootstrap.MigrateOrAdoptAsync(db, "organizations");
        await DatabaseBootstrap.MigrateOrAdoptAsync(db, "organizations");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('IX_users_UserHandle_NoCase', 'IX_user_handle_history_UserHandle_NoCase')";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        names.Should().BeEquivalentTo(["IX_users_UserHandle_NoCase", "IX_user_handle_history_UserHandle_NoCase"]);
    }
}
