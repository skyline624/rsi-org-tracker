using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>Out-of-range paging values are brought back into range instead of failing or dumping tables.</summary>
[Collection(ApiCollection.Name)]
public class PagingBoundsTests(ApiFactory factory)
{
    [Fact]
    public async Task Limit_IsCappedAt500()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.ChangeEvents.AddRange(Enumerable.Range(0, 520).Select(i => new ChangeEvent
            {
                Timestamp = DateTime.UtcNow, EntityType = "organization", EntityId = "BOUNDS",
                ChangeType = "bounds_test", OrgSid = "BOUNDS", NewValue = i.ToString(),
            }));
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("paging-limit");

        var feed = await client.GetFromJsonAsync<JsonElement>("/api/changes?orgSid=BOUNDS&limit=100000");

        feed.GetArrayLength().Should().Be(500);
    }

    [Fact]
    public async Task OrganizationHistory_IsBounded_NewestFirst()
    {
        // Deferred from the final review: every snapshot of the org, texts included.
        var start = DateTime.UtcNow.AddDays(-10);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Organizations.AddRange(Enumerable.Range(0, 5).Select(i => new Organization
            {
                Sid = "HISTORY", Name = $"Name {i}", Timestamp = start.AddDays(i), MembersCount = i,
                ContentCollected = true, Description = new string('x', 5_000),
            }));
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("paging-history");

        var history = await client.GetFromJsonAsync<JsonElement>("/api/organizations/HISTORY/history?limit=2");

        history.EnumerateArray().Select(o => o.GetProperty("name").GetString()).Should().Equal("Name 4", "Name 3");
    }

    [Theory]
    [InlineData("/api/organizations?page=-2&pageSize=0", 1, 1)]
    [InlineData("/api/organizations?page=1&pageSize=100000", 1, 200)]
    [InlineData("/api/users?page=0&pageSize=-5", 1, 1)]
    [InlineData("/api/users?search=zz&page=-1&pageSize=100000", 1, 200)]
    public async Task PageAndPageSize_AreBroughtIntoRange(string url, int page, int pageSize)
    {
        var client = await factory.SignedInClientAsync($"paging-page-{Guid.NewGuid():N}");

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("page").GetInt32().Should().Be(page);
        body.GetProperty("pageSize").GetInt32().Should().Be(pageSize);
    }

    [Theory]
    [InlineData("/api/changes/summary?days=0")]
    [InlineData("/api/changes/summary?days=100000")]
    [InlineData("/api/changes/types/bounds_test?limit=-1")]
    [InlineData("/api/stats/timeline?days=-3")]
    public async Task OtherBounds_AnswerOk(string url)
    {
        var client = await factory.SignedInClientAsync($"paging-other-{Guid.NewGuid():N}");

        (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
