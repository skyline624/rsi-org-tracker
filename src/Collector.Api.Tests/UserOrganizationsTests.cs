using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class UserOrganizationsTests(ApiFactory factory)
{
    [Fact]
    public async Task EachMembership_CarriesTheOrgsLatestName()
    {
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.Organizations.AddRange(
                new Organization { Sid = "NAMED", Name = "Old name", Timestamp = now.AddDays(-2) },
                new Organization { Sid = "NAMED", Name = "New name", Timestamp = now.AddDays(-1) });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "NAMED", UserHandle = "named-pilot", Timestamp = now });
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("user-orgs");

        var orgs = await client.GetFromJsonAsync<JsonElement>("/api/users/named-pilot/organizations");

        orgs.EnumerateArray().Single().GetProperty("orgName").GetString().Should().Be("New name");
    }
}
