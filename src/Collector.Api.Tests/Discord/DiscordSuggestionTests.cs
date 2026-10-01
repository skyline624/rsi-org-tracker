using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// GET api/discord/guilds/{guildId}/suggestions (spec § 10.1): Discord name tokens matched
/// against RSI handles regardless of case, carrying the canonical current handle and the
/// citizen id read from the database; strong only for the mapped org's active roster.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordSuggestionTests(ApiFactory factory)
{
    private static int _seq = 51_000;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_510_000_000 + n * 10 + k;

    [Fact]
    public async Task ACaseMismatch_IsSuggested_WithTheCanonicalHandleAndCitizenId()
    {
        var n = Next();
        await SeedUserAsync(Cid(n, 0), $"Pilote{n}", "Pilote affiché");
        var guildId = await SeedGuildAsync(orgSid: null);
        var userId = await SeedMemberAsync(guildId, $"pilote{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-case-{n}");

        var suggestion = (await SuggestionsAsync(client, guildId)).Should().ContainSingle().Subject;

        suggestion.GetProperty("discordUserId").GetString().Should().Be(userId);
        suggestion.GetProperty("discordName").GetString().Should().Be($"pilote{n}");
        suggestion.GetProperty("matchedToken").GetString().Should().BeEquivalentTo($"pilote{n}");
        suggestion.GetProperty("handle").GetString().Should().Be($"Pilote{n}");
        suggestion.GetProperty("citizenId").GetInt32().Should().Be(Cid(n, 0));
        suggestion.GetProperty("displayName").GetString().Should().Be("Pilote affiché");
        suggestion.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task AFormerHandle_IsSuggested_WithTheCurrentHandleAndCitizenId()
    {
        var n = Next();
        await SeedUserAsync(Cid(n, 0), $"NewHandle{n}", null);
        await SeedHistoryAsync(Cid(n, 0), $"OldHandle{n}", DateTime.UtcNow.AddYears(-1));
        await SeedHistoryAsync(Cid(n, 0), $"NewHandle{n}", DateTime.UtcNow);
        var guildId = await SeedGuildAsync(orgSid: null);
        var userId = await SeedMemberAsync(guildId, $"c5user{n}", nick: $"[CORP] oldhandle{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-old-{n}");

        var suggestion = (await SuggestionsAsync(client, guildId)).Should().ContainSingle().Subject;

        suggestion.GetProperty("discordUserId").GetString().Should().Be(userId);
        suggestion.GetProperty("discordName").GetString().Should().Be($"[CORP] oldhandle{n}");
        suggestion.GetProperty("matchedToken").GetString().Should().BeEquivalentTo($"oldhandle{n}");
        suggestion.GetProperty("handle").GetString().Should().Be($"NewHandle{n}");
        suggestion.GetProperty("citizenId").GetInt32().Should().Be(Cid(n, 0));
        suggestion.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task InAMappedGuild_TheActiveRosterIsStrong_OtherMatchesMedium()
    {
        var n = Next();
        var sid = $"SG{n}";
        await SeedOrgMemberAsync(sid, $"Orgy{n}", Cid(n, 0), active: true);
        await SeedOrgMemberAsync(sid, $"Gone{n}", Cid(n, 1), active: false);
        await SeedUserAsync(Cid(n, 0), $"Orgy{n}", "Orgy");
        await SeedUserAsync(Cid(n, 1), $"Gone{n}", null);
        var mapped = await SeedGuildAsync(orgSid: sid);
        var strongId = await SeedMemberAsync(mapped, $"c5strong{n}", nick: $"[{sid}] orgy{n}");
        var mediumId = await SeedMemberAsync(mapped, $"gone{n}");
        var unmapped = await SeedGuildAsync(orgSid: null);
        var unmappedId = await SeedMemberAsync(unmapped, $"orgy{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-strong-{n}");

        var inMapped = await SuggestionsAsync(client, mapped);
        var inUnmapped = await SuggestionsAsync(client, unmapped);

        inMapped.Select(s => (
                s.GetProperty("discordUserId").GetString(),
                s.GetProperty("handle").GetString(),
                s.GetProperty("citizenId").GetInt32(),
                s.GetProperty("confidence").GetString()))
            .Should().Equal(
                (strongId, $"Orgy{n}", Cid(n, 0), "strong"),
                (mediumId, $"Gone{n}", Cid(n, 1), "medium"));
        var single = inUnmapped.Should().ContainSingle().Subject;
        single.GetProperty("discordUserId").GetString().Should().Be(unmappedId);
        single.GetProperty("confidence").GetString().Should().Be("medium");
    }

    [Fact]
    public async Task BotsDepartedLinkedAndRejectedMembers_AreNotSuggested()
    {
        var n = Next();
        string[] names = ["Kept", "Bot", "Left", "Linked", "RejCid", "RejHandle"];
        for (var k = 0; k < names.Length; k++) await SeedUserAsync(Cid(n, k), $"{names[k]}{n}", null);
        var guildId = await SeedGuildAsync(orgSid: null);
        var kept = await SeedMemberAsync(guildId, $"kept{n}");
        await SeedMemberAsync(guildId, $"bot{n}", bot: true);
        await SeedMemberAsync(guildId, $"left{n}", left: true);
        var linked = await SeedMemberAsync(guildId, $"linked{n}");
        var rejectedById = await SeedMemberAsync(guildId, $"rejcid{n}");
        var rejectedByHandle = await SeedMemberAsync(guildId, $"rejhandle{n}");
        await SeedLinkAsync(linked);
        await SeedRejectionAsync(rejectedById, Cid(n, 4).ToString(CultureInfo.InvariantCulture));
        // A rejection saved under the handle key still hides a suggestion that now has a citizen id.
        await SeedRejectionAsync(rejectedByHandle, $"h:rejhandle{n}");
        var client = await factory.SignedInClientAsync($"c5-sugg-skip-{n}");

        var suggestions = await SuggestionsAsync(client, guildId);

        suggestions.Select(s => s.GetProperty("discordUserId").GetString()).Should().Equal(kept);
    }

    [Fact]
    public async Task AnUnknownGuild_Returns404()
    {
        var client = await factory.SignedInClientAsync($"c5-sugg-404-{Next()}");

        var response = await client.GetAsync($"/api/discord/guilds/{DiscordTestKit.NewSnowflake()}/suggestions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<List<JsonElement>> SuggestionsAsync(HttpClient client, string guildId)
    {
        var response = await client.GetAsync($"/api/discord/guilds/{guildId}/suggestions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
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

    private Task SeedOrgMemberAsync(string sid, string handle, int? citizenId, bool active) => WithDbAsync(async db =>
    {
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Timestamp = DateTime.UtcNow, IsActive = active,
        });
        await db.SaveChangesAsync();
    });

    private async Task<string> SeedGuildAsync(string? orgSid)
    {
        var guildId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordGuilds.Add(new DiscordGuild
            {
                GuildId = guildId, Name = $"Guild {guildId}", OrgSid = orgSid,
                FirstSyncAt = now, LastSyncAt = now, LastCollectedAt = now, LastCompleteSyncAt = now,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });
        return guildId;
    }

    private async Task<string> SeedMemberAsync(
        string guildId, string username, string? nick = null, bool bot = false, bool left = false)
    {
        var userId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.DiscordAccounts.Add(new DiscordAccount
            {
                DiscordUserId = userId, Username = username, IsBot = bot, FirstSeenAt = now, LastSeenAt = now,
            });
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guildId, DiscordUserId = userId, Nick = nick, RoleIdsJson = "[]",
                FirstSeenAt = now, LastSeenAt = now, LeftAt = left ? now : null,
            });
            await db.SaveChangesAsync();
        });
        return userId;
    }

    private Task SeedLinkAsync(string discordUserId) => WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var entity = new TrackedEntity { CurrentHandle = $"linked-{discordUserId}", CreatedAt = now, UpdatedAt = now };
        db.TrackedEntities.Add(entity);
        await db.SaveChangesAsync();
        db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = discordUserId,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    });

    private Task SeedRejectionAsync(string discordUserId, string citizenKey) => WithDbAsync(async db =>
    {
        db.DiscordLinkRejections.Add(new DiscordLinkRejection
        {
            DiscordUserId = discordUserId, CitizenKey = citizenKey, ByApiUserId = 0, ByUsername = "seed",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    });
}
