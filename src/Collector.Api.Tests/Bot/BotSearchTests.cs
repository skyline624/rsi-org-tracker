using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>/api/bot/search: its two halves, the member counts of its orgs, and what it never searches (spec § 5.2).</summary>
[Collection(ApiCollection.Name)]
public class BotSearchTests(ApiFactory factory)
{
    private static string NewWord() => "w" + Guid.NewGuid().ToString("N")[..9];

    private async Task<(HttpStatusCode Status, JsonElement Body)> SearchAsync(string query, string command = "recherche")
    {
        var key = await CreateBotKeyAsync(factory);
        var response = await factory.CreateClient().SendAsync(Get($"/api/bot/search?{query}", key, command));
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
        return (response.StatusCode, body);
    }

    private static List<string> Sids(JsonElement body) =>
        body.GetProperty("orgs").EnumerateArray().Select(o => o.GetProperty("sid").GetString()!).ToList();

    private static List<string> Handles(JsonElement body) =>
        body.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("handle").GetString()!).ToList();

    /// <summary>An org and a citizen both found by the same word.</summary>
    private async Task<(string Word, string Sid, string Handle)> SeedOrgAndPlayerAsync()
    {
        var word = NewWord();
        var sid = word.ToUpperInvariant();
        var handle = word + "pilot";
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Kind Corp", Timestamp = DateTime.UtcNow, MembersCount = 3 });
            db.Users.Add(new User { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        });
        return (word, sid, handle);
    }

    [Fact]
    public async Task WithoutKind_BothHalvesAnswer()
    {
        var (word, sid, handle) = await SeedOrgAndPlayerAsync();

        var (status, body) = await SearchAsync($"q={word}");

        status.Should().Be(HttpStatusCode.OK);
        Sids(body).Should().Equal(sid);
        Handles(body).Should().Equal(handle);
    }

    [Fact]
    public async Task KindOrgs_ReturnsNoPlayers()
    {
        var (word, sid, _) = await SeedOrgAndPlayerAsync();

        var (status, body) = await SearchAsync($"q={word}&kind=orgs", "autocomplete");

        status.Should().Be(HttpStatusCode.OK);
        Sids(body).Should().Equal(sid);
        Handles(body).Should().BeEmpty();
    }

    [Fact]
    public async Task KindPlayers_ReturnsNoOrgs()
    {
        var (word, _, handle) = await SeedOrgAndPlayerAsync();

        var (status, body) = await SearchAsync($"q={word}&kind=players", "autocomplete");

        status.Should().Be(HttpStatusCode.OK);
        Sids(body).Should().BeEmpty();
        Handles(body).Should().Equal(handle);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ORGS")]
    [InlineData("player")]
    public async Task AnUnknownKind_IsA400(string kind)
    {
        (await SearchAsync($"q=ab&kind={kind}")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnOrgWithSeveralSnapshots_ReportsTheLatestMembersCount()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Grown Corp", Timestamp = DateTime.UtcNow.AddDays(-3), MembersCount = 5 });
            db.Organizations.Add(new Organization { Sid = sid, Name = "Grown Corp", Timestamp = DateTime.UtcNow, MembersCount = 9 });
        });

        var (status, body) = await SearchAsync($"q={sid}&kind=orgs");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("orgs").EnumerateArray().Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("membersCount").GetInt32()))
            .Should().Equal((sid, 9));
    }

    [Fact]
    public async Task ASearchWithoutAnyHit_StillAnswers()
    {
        var (status, body) = await SearchAsync($"q={NewWord()}");

        status.Should().Be(HttpStatusCode.OK);
        Sids(body).Should().BeEmpty();
        Handles(body).Should().BeEmpty();
    }

    [Fact]
    public async Task Players_AreFoundByHandleOrDisplayName_AndRosterOnlyHandlesByPrefix()
    {
        var word = NewWord();
        var byHandle = "x" + word;                 // substring of a users handle
        var byDisplayName = NewHandle();           // the word is only in the display name
        var rosterOnly = word + "roster";          // prefix of a handle seen only in a roster
        var rosterSubstring = "y" + word;          // a roster-only handle is matched by prefix only
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = byHandle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Users.Add(new User { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = byDisplayName, DisplayName = $"The {word} One", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "BOTSEARCH", UserHandle = rosterOnly, DisplayName = "Roster", Timestamp = DateTime.UtcNow, IsActive = true });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "BOTSEARCH", UserHandle = rosterSubstring, Timestamp = DateTime.UtcNow, IsActive = true });
        });

        var (status, body) = await SearchAsync($"q={word}&kind=players");

        status.Should().Be(HttpStatusCode.OK);
        Handles(body).Should().BeEquivalentTo(byHandle, byDisplayName, rosterOnly);
        body.GetProperty("players").EnumerateArray()
            .Single(p => p.GetProperty("handle").GetString() == byDisplayName)
            .GetProperty("displayName").GetString().Should().Be($"The {word} One");
    }

    [Fact]
    public async Task APlayerReachableOnlyThroughAStaffNote_IsNotFoundByTheBot_ButStillByTheSite()
    {
        var word = NewWord();
        var handle = NewHandle();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            var entity = new TrackedEntity { CurrentHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.TrackedEntities.Add(entity);
            await db.SaveChangesAsync();
            db.EntityNotes.Add(new EntityNote
            {
                TrackedEntityId = entity.Id, AuthorApiUserId = 1, AuthorUsername = "staff",
                Body = $"Seen recruiting for {word} last week", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (status, body) = await SearchAsync($"q={word}");
        var site = await (await factory.SignedInClientAsync($"notes-{Guid.NewGuid():N}"))
            .GetFromJsonAsync<JsonElement>($"/api/users?search={word}");

        status.Should().Be(HttpStatusCode.OK);
        Handles(body).Should().BeEmpty();
        site.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userHandle").GetString()).Should().Contain(handle);
    }

    [Fact]
    public async Task APlayerKnownOnlyAsATrackedEntity_IsNotFoundByTheBot_ButStillByTheSite()
    {
        var word = NewWord();
        var handle = word + "tracked";
        await SeedAsync(factory, db =>
            db.TrackedEntities.Add(new TrackedEntity { CurrentHandle = handle, Source = TrackedEntitySource.Manual, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }));

        var (status, body) = await SearchAsync($"q={word}");
        var site = await (await factory.SignedInClientAsync($"tracked-{Guid.NewGuid():N}"))
            .GetFromJsonAsync<JsonElement>($"/api/users?search={word}");

        status.Should().Be(HttpStatusCode.OK);
        Handles(body).Should().BeEmpty();
        site.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userHandle").GetString()).Should().Contain(handle);
    }
}
