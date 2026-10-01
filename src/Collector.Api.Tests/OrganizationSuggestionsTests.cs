using System.Net;
using System.Net.Http.Json;
using Collector.Api.Dtos.Organizations;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class OrganizationSuggestionsTests(ApiFactory factory)
{
    private async Task<HttpClient> ClientAsync() =>
        await factory.SignedInClientAsync($"org-lookup-{Guid.NewGuid():N}");

    private async Task SeedAsync(params Organization[] orgs)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        foreach (var org in orgs)
            if (!await db.Organizations.AnyAsync(o => o.Sid == org.Sid && o.Timestamp == org.Timestamp))
                db.Organizations.Add(org);
        await db.SaveChangesAsync();
    }

    private static Organization Org(string sid, string name, int day = 1) =>
        new() { Sid = sid, Name = name, Timestamp = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc) };

    private static async Task<OrganizationSuggestionDto[]> SearchAsync(HttpClient client, string text) =>
        (await client.GetFromJsonAsync<OrganizationSuggestionDto[]>(
            $"/api/organizations/suggestions?query={Uri.EscapeDataString(text)}"))!;

    [Theory]
    [InlineData("Aurelis Ops")]
    [InlineData("AURELIS-OPS")]
    [InlineData("aurelis_ops")]
    [InlineData("AURELISOPS")]
    [InlineData("⭐ 𝐀𝐔𝐑𝐄𝐋𝐈𝐒-𝐎𝐏𝐒 ⭐")]
    [InlineData("ＡＵＲＥＬＩＳ　ＯＰＳ")]
    [InlineData("aurelis")]
    public async Task Aurelis_IsFoundByNameSidAndDecoratedDiscordNames(string text)
    {
        await SeedAsync(Org("AURELIS", "Aurelis Ops"));
        using var client = await ClientAsync();
        (await SearchAsync(client, text)).Should().ContainSingle()
            .Which.Should().Be(new OrganizationSuggestionDto("AURELIS", "Aurelis Ops"));
    }

    [Fact]
    public async Task OnlyTheLatestNameIsSuggested_AndSeparatorsInStoredNamesAreIgnored()
    {
        await SeedAsync(Org("LOOKLATE", "Ancienne recherche exclusive"),
            Org("LOOKLATE", "Corpo—actuelle.test", 2));
        using var client = await ClientAsync();
        (await SearchAsync(client, "Ancienne recherche exclusive")).Should().BeEmpty();
        (await SearchAsync(client, "Corpo actuelle test")).Should().ContainSingle()
            .Which.Should().Be(new OrganizationSuggestionDto("LOOKLATE", "Corpo—actuelle.test"));
    }

    [Fact]
    public async Task ExactSidIsFirst_EvenWhenMoreThanTenNamesMatch()
    {
        await SeedAsync(Enumerable.Range(0, 12).Select(i => Org($"LOK{i:D2}", $"ZZLOOK groupe {i}"))
            .Append(Org("ZZLOOK", "Nom différent")).ToArray());
        using var client = await ClientAsync();
        var results = await SearchAsync(client, " zzlook ");
        results.Should().HaveCount(10);
        results[0].Should().Be(new OrganizationSuggestionDto("ZZLOOK", "Nom différent"));
        results.Select(o => o.Sid).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ExactNameIsFirstAmongSimilarNames_AndOneCharacterSidIsSupported()
    {
        await SeedAsync(Org("LOOKEXACT", "Lookup exact"), Org("LOOKOTHER", "Lookup exact exploration"),
            Org("X", "Single character"));
        using var client = await ClientAsync();
        (await SearchAsync(client, "lookup-exact"))[0].Sid.Should().Be("LOOKEXACT");
        (await SearchAsync(client, "x"))[0].Sid.Should().Be("X");
    }

    [Fact]
    public async Task EmptyOrDecorativeQueriesDoNotReturnEveryOrganization_AndLengthIsBounded()
    {
        using var client = await ClientAsync();
        foreach (var text in new[] { "", " ", "%_", "⭐ -- '" })
            (await SearchAsync(client, text)).Should().BeEmpty();
        (await client.GetAsync($"/api/organizations/suggestions?query={new string('a', 101)}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/organizations/suggestions?query=Aurelis"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
