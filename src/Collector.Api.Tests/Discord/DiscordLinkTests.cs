using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// POST api/discord/links, POST/DELETE api/discord/link-rejections (spec § 10.1), and the
/// snowflake rule of the generic links route. A link is made on the entity of the person
/// read back from the database, under their canonical current handle: a Discord spelling or
/// a former handle never reaches tracked_entities.CurrentHandle.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordLinkTests(ApiFactory factory)
{
    private static int _seq = 52_000;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_520_000_000 + n * 10 + k;

    [Fact]
    public async Task AnOptedOutAccount_CannotBeSuggestedRelinkedOrRejected_AfterInterruptedErasure()
    {
        var n = Next();
        var handle = $"OptedOut{n}";
        var cid = Cid(n, 0);
        await SeedUserAsync(cid, handle, null);
        var guildId = await SeedGuildAsync();
        var discordUserId = await SeedMemberAsync(guildId, handle);
        await WithDbAsync(async db =>
        {
            db.DiscordOptOuts.Add(new DiscordOptOut
            {
                DiscordUserId = discordUserId, CreatedAt = DateTime.UtcNow, ByUsername = "admin",
            });
            await db.SaveChangesAsync();
        });
        var client = await factory.SignedInClientAsync($"c5-optout-{n}");

        (await SuggestionCountAsync(client, guildId)).Should().Be(0);
        var body = new { discordUserId, citizenId = cid, handle };
        (await client.PostAsJsonAsync("/api/discord/links", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/discord/link-rejections", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"/api/users/{handle}/links", new { provider = "discord", value = discordUserId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadAsync(db => db.EntityLinks.AnyAsync(l => l.Provider == LinkProviders.Discord && l.Value == discordUserId)))
            .Should().BeFalse();
        (await ReadAsync(db => db.DiscordLinkRejections.AnyAsync(r => r.DiscordUserId == discordUserId))).Should().BeFalse();
        (await ReadAsync(db => db.TrackedEntities.AnyAsync(e => e.CitizenId == cid))).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptingAFormerHandle_LinksTheCitizensEntity_AndKeepsItsCurrentHandle()
    {
        var n = Next();
        var cid = Cid(n, 0);
        await SeedUserAsync(cid, $"NewHandle{n}", "New display");
        await SeedHistoryAsync(cid, $"OldHandle{n}", DateTime.UtcNow.AddYears(-1));
        var entityId = await SeedEntityAsync(cid, $"NewHandle{n}", "New display");
        var userId = DiscordTestKit.NewSnowflake();
        var client = await factory.SignedInClientAsync($"c5-link-old-{n}");

        var first = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = (int?)null, handle = $"oldhandle{n}" });
        var again = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = cid, handle = $"oldhandle{n}" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        again.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("entityId").GetInt64().Should().Be(entityId);
        body.GetProperty("handle").GetString().Should().Be($"NewHandle{n}");
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entityId").GetInt64().Should().Be(entityId);

        var entities = await ReadAsync(db => db.TrackedEntities.AsNoTracking().Where(e => e.CitizenId == cid).ToListAsync());
        entities.Should().ContainSingle().Which.CurrentHandle.Should().Be($"NewHandle{n}");
        var oldHandle = $"oldhandle{n}";
        (await ReadAsync(db => db.TrackedEntities.AnyAsync(e => e.CurrentHandle == oldHandle))).Should().BeFalse();
        (await ReadAsync(db => db.EntityLinks.CountAsync(l =>
                l.TrackedEntityId == entityId && l.Provider == LinkProviders.Discord && l.Value == userId)))
            .Should().Be(1, "linking twice is idempotent");
    }

    [Fact]
    public async Task ACaseMismatchedHandle_CreatesTheEntityUnderTheCanonicalHandle()
    {
        var n = Next();
        var cid = Cid(n, 0);
        await SeedUserAsync(cid, $"Pilote{n}", "Pilote affiché");
        var userId = DiscordTestKit.NewSnowflake();
        var client = await factory.SignedInClientAsync($"c5-link-case-{n}");

        var response = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = (int?)null, handle = $"pilote{n}" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("handle").GetString().Should().Be($"Pilote{n}");
        var entity = (await ReadAsync(db => db.TrackedEntities.AsNoTracking().Where(e => e.CitizenId == cid).ToListAsync()))
            .Should().ContainSingle().Subject;
        entity.CurrentHandle.Should().Be($"Pilote{n}");
        (await ReadAsync(db => db.EntityLinks.CountAsync(l => l.TrackedEntityId == entity.Id && l.Value == userId)))
            .Should().Be(1);
    }

    [Fact]
    public async Task ARosterOnlyPerson_IsLinkedThroughTheirOrgMemberRow()
    {
        var n = Next();
        await SeedOrgMemberAsync($"LK{n}", $"Roster{n}", citizenId: null);
        var userId = DiscordTestKit.NewSnowflake();
        var client = await factory.SignedInClientAsync($"c5-link-roster-{n}");

        var response = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = userId, citizenId = (int?)null, handle = $"Roster{n}" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("handle").GetString().Should().Be($"Roster{n}");
        var entityId = body.GetProperty("entityId").GetInt64();
        var entity = await ReadAsync(db => db.TrackedEntities.AsNoTracking().SingleAsync(e => e.Id == entityId));
        entity.CurrentHandle.Should().Be($"Roster{n}");
        entity.CitizenId.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownHandle_Returns404_AndCreatesNoEntity()
    {
        var n = Next();
        var handle = $"Nobody{n}";
        var client = await factory.SignedInClientAsync($"c5-link-404-{n}");

        var response = await client.PostAsJsonAsync("/api/discord/links",
            new { discordUserId = DiscordTestKit.NewSnowflake(), citizenId = (int?)null, handle });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(db => db.TrackedEntities.AnyAsync(e => e.CurrentHandle == handle))).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidBodies_Return400()
    {
        var n = Next();
        var client = await factory.SignedInClientAsync($"c5-link-400-{n}");

        (await client.PostAsJsonAsync("/api/discord/links",
                new { discordUserId = "not-a-snowflake", citizenId = (int?)null, handle = $"Pilote{n}" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/discord/links",
                new { discordUserId = DiscordTestKit.NewSnowflake(), citizenId = (int?)null, handle = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/discord/link-rejections",
                new { discordUserId = "123", citizenId = (int?)null, handle = $"Pilote{n}" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARejection_HidesTheSuggestion_UntilItIsUndone()
    {
        var n = Next();
        await SeedUserAsync(Cid(n, 0), $"Reject{n}", null);
        var guildId = await SeedGuildAsync();
        var userId = await SeedMemberAsync(guildId, $"reject{n}");
        var client = await factory.SignedInClientAsync($"c5-reject-{n}");
        var rejection = new { discordUserId = userId, citizenId = Cid(n, 0), handle = $"Reject{n}" };
        (await SuggestionCountAsync(client, guildId)).Should().Be(1);

        var created = await client.PostAsJsonAsync("/api/discord/link-rejections", rejection);
        var again = await client.PostAsJsonAsync("/api/discord/link-rejections", rejection);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64().Should().Be(id);
        (await SuggestionCountAsync(client, guildId)).Should().Be(0);

        (await client.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SuggestionCountAsync(client, guildId)).Should().Be(1);
    }

    [Fact]
    public async Task OnlyItsAuthorOrAnAdmin_CanUndoARejection()
    {
        var n = Next();
        var author = await factory.SignedInClientAsync($"c5-rej-author-{n}");
        var other = await factory.SignedInClientAsync($"c5-rej-other-{n}");
        var admin = await factory.SignedInClientAsync($"c5-rej-admin-{n}", isAdmin: true);
        var created = await author.PostAsJsonAsync("/api/discord/link-rejections",
            new { discordUserId = DiscordTestKit.NewSnowflake(), citizenId = (int?)null, handle = $"Someone{n}" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        (await other.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/discord/link-rejections/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheGenericLinksRoute_RefusesADiscordValueThatIsNotASnowflake()
    {
        var n = Next();
        var client = await factory.SignedInClientAsync($"c5-links-route-{n}");
        var url = $"/api/users/c5linkroute{n}/links";

        (await client.PostAsJsonAsync(url, new { provider = "discord", value = "pilote#1234" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new { provider = "discord", value = "1234567890123456" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(url, new { provider = "discord", value = DiscordTestKit.NewSnowflake() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(url, new { provider = "uex", value = "not-a-number" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<int> SuggestionCountAsync(HttpClient client, string guildId)
    {
        var response = await client.GetAsync($"/api/discord/guilds/{guildId}/suggestions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
    }

    private async Task<T> ReadAsync<T>(Func<TrackerDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task SeedUserAsync(int citizenId, string handle, string? displayName) => WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, DisplayName = displayName, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    });

    private Task SeedHistoryAsync(int citizenId, string handle, DateTime lastSeen) => WithDbAsync(async db =>
    {
        db.UserHandleHistories.Add(new UserHandleHistory
        {
            CitizenId = citizenId, UserHandle = handle, FirstSeen = lastSeen.AddDays(-30), LastSeen = lastSeen,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedOrgMemberAsync(string sid, string handle, int? citizenId) => WithDbAsync(async db =>
    {
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Timestamp = DateTime.UtcNow, IsActive = true,
        });
        await db.SaveChangesAsync();
    });

    private async Task<long> SeedEntityAsync(int citizenId, string handle, string? displayName)
    {
        long id = 0;
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var entity = new TrackedEntity
            {
                CitizenId = citizenId, CurrentHandle = handle, DisplayName = displayName,
                Source = TrackedEntitySource.Collected, Status = TrackedEntityStatus.Active,
                CreatedAt = now, UpdatedAt = now,
            };
            db.TrackedEntities.Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    private async Task<string> SeedGuildAsync()
    {
        var guildId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordGuilds.Add(new DiscordGuild
            {
                GuildId = guildId, Name = $"Guild {guildId}",
                FirstSyncAt = now, LastSyncAt = now, LastCollectedAt = now, LastCompleteSyncAt = now,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        return guildId;
    }

    private async Task<string> SeedMemberAsync(string guildId, string username)
    {
        var userId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordAccounts.Add(new DiscordAccount
            {
                DiscordUserId = userId, Username = username, FirstSeenAt = now, LastSeenAt = now,
            });
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guildId, DiscordUserId = userId, RoleIdsJson = "[]", FirstSeenAt = now, LastSeenAt = now,
            });
            await db.SaveChangesAsync();
        });
        return userId;
    }
}
