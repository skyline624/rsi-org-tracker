using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>
/// The user search unions users, roster handles (32 M rows) and tracked entities:
/// it needs two characters, and its count stops at 1001 ("more than 1000").
/// </summary>
[Collection(ApiCollection.Name)]
public class UserSearchBoundsTests(ApiFactory factory)
{
    [Fact]
    public async Task AOneCharacterSearch_FindsNothing()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Users.Add(new User { CitizenId = 899_999, UserHandle = "q-single", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync($"search-short-{Guid.NewGuid():N}");

        var body = await client.GetFromJsonAsync<JsonElement>("/api/users?search=q");

        body.GetProperty("total").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TheCount_StopsAt1001()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Users.AddRange(Enumerable.Range(0, 1100).Select(i => new User
            {
                CitizenId = 900_000 + i, UserHandle = $"capcount{i:D4}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }));
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync($"search-cap-{Guid.NewGuid():N}");

        var body = await client.GetFromJsonAsync<JsonElement>("/api/users?search=capcount&pageSize=50");

        body.GetProperty("total").GetInt32().Should().Be(1001);
        body.GetProperty("items").GetArrayLength().Should().Be(50);
    }
}
