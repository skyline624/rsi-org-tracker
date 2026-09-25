using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>/members?status=active|former|all is paginated by the server (latest row per member).</summary>
[Collection(ApiCollection.Name)]
public class OrgMembersPagingTests(ApiFactory factory)
{
    private static bool _seeded;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    private async Task<HttpClient> ClientAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (!_seeded)
            {
                var now = DateTime.UtcNow;
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                OrganizationMember Row(string handle, int daysAgo, bool active) => new()
                {
                    OrgSid = "ROSTER", UserHandle = handle, Timestamp = now.AddDays(-daysAgo), IsActive = active, Rank = "Pilot",
                };
                db.OrganizationMembers.AddRange(
                    Row("alice", 1, true), Row("alice", 5, false),
                    Row("Bob", 1, true),
                    Row("carol", 3, false),
                    Row("dave", 9, false), Row("dave", 1, true));
                await db.SaveChangesAsync();
                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }
        return await factory.SignedInClientAsync($"org-members-{Guid.NewGuid():N}");
    }

    private static (List<string?> Handles, int Total) Page(JsonElement body)
        => (body.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("userHandle").GetString()).ToList(),
            body.GetProperty("total").GetInt32());

    [Theory]
    [InlineData("active", 1, 2, new[] { "alice", "Bob" }, 3)]
    [InlineData("active", 2, 2, new[] { "dave" }, 3)]
    [InlineData("former", 1, 50, new[] { "carol" }, 1)]
    [InlineData("all", 1, 50, new[] { "alice", "Bob", "carol", "dave" }, 4)]
    public async Task EachStatus_IsPagedInHandleOrder(string status, int page, int pageSize, string[] handles, int total)
    {
        var client = await ClientAsync();

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/organizations/ROSTER/members?status={status}&page={page}&pageSize={pageSize}");

        var (actual, actualTotal) = Page(body);
        actual.Should().Equal(handles);
        actualTotal.Should().Be(total);
    }

    [Fact]
    public async Task WithoutStatus_TheCurrentMembersAreServed()
    {
        var client = await ClientAsync();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/organizations/ROSTER/members");

        Page(body).Handles.Should().Equal("alice", "Bob", "dave");
    }

    [Fact]
    public async Task AtATime_TheRosterAsItWasThenIsPaged()
    {
        var client = await ClientAsync();
        var fourDaysAgo = DateTime.UtcNow.AddDays(-4).ToString("o");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/organizations/ROSTER/members?status=all&at_time={Uri.EscapeDataString(fourDaysAgo)}");

        // Four days ago: alice (5 days), carol (3 days) not yet, dave (9 days), Bob not yet.
        Page(body).Handles.Should().Equal("alice", "dave");
    }

    [Fact]
    public async Task AnUnknownStatus_IsABadRequest()
    {
        var client = await ClientAsync();

        (await client.GetAsync("/api/organizations/ROSTER/members?status=everyone")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }
}
