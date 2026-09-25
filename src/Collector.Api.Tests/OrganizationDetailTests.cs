using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>
/// Listing snapshots (Phase 1) carry no page texts, content snapshots (Phase 2) do:
/// the detail combines the latest of each, the list reads no long text.
/// </summary>
[Collection(ApiCollection.Name)]
public class OrganizationDetailTests(ApiFactory factory)
{
    private async Task SeedAsync(string sid)
    {
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        db.Organizations.AddRange(
            new Organization
            {
                Sid = sid, Name = "Old name", Timestamp = now.AddDays(-10), MembersCount = 5,
                ContentCollected = true, Description = "About us", FocusPrimaryName = "Trading",
            },
            new Organization { Sid = sid, Name = "New name", Timestamp = now.AddDays(-1), MembersCount = 7 });
        foreach (var handle in new[] { "one", "two" })
        {
            db.MemberCollectionLogs.Add(new MemberCollectionLog { OrgSid = sid, CollectionTime = now.AddHours(-1), UserHandle = handle });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Detail_TakesTheTextsFromTheLastContentSnapshot_AndRsisMemberCount()
    {
        await SeedAsync("DETAIL");
        var client = await factory.SignedInClientAsync("org-detail");

        var org = await client.GetFromJsonAsync<JsonElement>("/api/organizations/DETAIL");

        org.GetProperty("name").GetString().Should().Be("New name");
        org.GetProperty("description").GetString().Should().Be("About us");
        org.GetProperty("focusPrimaryName").GetString().Should().Be("Trading");
        org.GetProperty("membersCount").GetInt32().Should().Be(7, "masked members count too; the roster log only has visible ones");
    }

    [Fact]
    public async Task List_CarriesNoLongTexts()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Organizations.Add(new Organization
            {
                Sid = "LISTED", Name = "Listed", Timestamp = DateTime.UtcNow, MembersCount = 3,
                ContentCollected = true, Description = "A long text the list does not need",
            });
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("org-list");

        var page = await client.GetFromJsonAsync<JsonElement>("/api/organizations?search=LISTED");

        var item = page.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("name").GetString().Should().Be("Listed");
        item.TryGetProperty("description", out var description).Should().BeTrue();
        description.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
