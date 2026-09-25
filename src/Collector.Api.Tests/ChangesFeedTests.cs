using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>
/// The /changes feeds are ordered by Id (newest recorded first): on 3 M events an
/// ORDER BY Timestamp without a usable index took 3.7 s, the rowid order is free.
/// </summary>
[Collection(ApiCollection.Name)]
public class ChangesFeedTests(ApiFactory factory)
{
    private async Task SeedAsync(string sid, string type)
    {
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        db.ChangeEvents.Add(new ChangeEvent { Timestamp = now, EntityType = "organization", EntityId = sid, ChangeType = type, OrgSid = sid, NewValue = "first recorded" });
        await db.SaveChangesAsync();
        db.ChangeEvents.Add(new ChangeEvent { Timestamp = now.AddHours(-1), EntityType = "organization", EntityId = sid, ChangeType = type, OrgSid = sid, NewValue = "last recorded" });
        await db.SaveChangesAsync();
    }

    private static List<string?> NewValues(JsonElement feed)
        => feed.EnumerateArray().Select(e => e.GetProperty("newValue").GetString()).ToList();

    [Fact]
    public async Task RecentChanges_NewestRecordedFirst()
    {
        await SeedAsync("FEEDA", "feed_test_a");
        var client = await factory.SignedInClientAsync("changes-feed-a");

        var feed = await client.GetFromJsonAsync<JsonElement>("/api/changes?orgSid=FEEDA&limit=10");

        NewValues(feed).Should().Equal("last recorded", "first recorded");
    }

    [Fact]
    public async Task ChangesByType_NewestRecordedFirst()
    {
        await SeedAsync("FEEDB", "feed_test_b");
        var client = await factory.SignedInClientAsync("changes-feed-b");

        var feed = await client.GetFromJsonAsync<JsonElement>("/api/changes/types/feed_test_b?limit=10");

        NewValues(feed).Should().Equal("last recorded", "first recorded");
    }
}
