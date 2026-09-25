using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>
/// Every stored date is UTC; SQLite gives them back without a zone. They must leave the
/// API marked UTC ("Z"), or browsers read them as local time.
/// </summary>
[Collection(ApiCollection.Name)]
public class UtcDatesTests(ApiFactory factory)
{
    [Fact]
    public async Task Dates_AreSerializedAsUtc()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Organizations.Add(new Organization { Sid = "UTCORG", Name = "Utc", Timestamp = DateTime.UtcNow, MembersCount = 1 });
            db.Users.Add(new User { CitizenId = 898_989, UserHandle = "utcpilot", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("utc-dates");

        var org = await client.GetFromJsonAsync<JsonElement>("/api/organizations/UTCORG");
        var search = await client.GetFromJsonAsync<JsonElement>("/api/users?search=utcpilot");

        org.GetProperty("timestamp").GetString().Should().EndWith("Z");
        search.GetProperty("items")[0].GetProperty("updatedAt").GetString().Should().EndWith("Z");
    }
}
