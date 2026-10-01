using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// PUT api/discord/guilds/{guildId}/org and PUT …/roles/{roleId} (spec § 11): anyone may
/// configure an unmapped guild; once mapped, only its responsible and admins may. Every edit
/// is audited, and a re-mapped guild is reconciled against its new org.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordGuildConfigTests(ApiFactory factory)
{
    private static int _seq = 62_000;

    private static int Next() => Interlocked.Increment(ref _seq);

    /// <summary>A citizen id no other test uses.</summary>
    private static int Cid(int n, int k) => 1_620_000_000 + n * 10 + k;

    private static string OrgUrl(string guildId) => $"/api/discord/guilds/{guildId}/org";

    private static string RoleUrl(string guildId, string roleId) => $"/api/discord/guilds/{guildId}/roles/{roleId}";

    [Fact]
    public async Task AnyUser_MapsAnUnmappedGuild_AndBecomesItsResponsible()
    {
        var n = Next();
        var sid = await SeedOrgAsync($"CFA{n}");
        var (guildId, _, _) = await IngestGuildAsync(n);
        var username = $"c6-map-{n}";
        var client = await factory.SignedInClientAsync(username);

        var response = await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = $"  cfa{n} " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgSid").GetString().Should().Be(sid);
        var guild = await GuildAsync(guildId);
        guild.OrgSid.Should().Be(sid);
        guild.OrgMappedByUsername.Should().Be(username);
        guild.OrgMappedByApiUserId.Should().Be(await ApiUserIdAsync(username));
        guild.OrgMappedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TheResponsible_RemapsAndClears_WhileAnotherUserGets403()
    {
        var n = Next();
        var sidA = await SeedOrgAsync($"CFA{n}");
        var sidB = await SeedOrgAsync($"CFB{n}");
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var owner = await factory.SignedInClientAsync($"c6-owner-{n}");
        var other = await factory.SignedInClientAsync($"c6-other-{n}");
        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await other.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 1, rsiRankLabel = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GuildAsync(guildId)).OrgSid.Should().Be(sidA);
        (await RoleAsync(guildId, roleId)).IsRank.Should().BeFalse();

        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GuildAsync(guildId)).OrgSid.Should().Be(sidB);

        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await GuildAsync(guildId);
        cleared.OrgSid.Should().BeNull();
        cleared.OrgMappedByApiUserId.Should().BeNull();
        cleared.OrgMappedByUsername.Should().BeNull();
        cleared.OrgMappedAt.Should().BeNull();

        // Unmapped again: anyone may map it, and becomes its responsible.
        (await other.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GuildAsync(guildId)).OrgMappedByUsername.Should().Be($"c6-other-{n}");
    }

    [Fact]
    public async Task AnAdmin_OverridesTheResponsible()
    {
        var n = Next();
        var sidA = await SeedOrgAsync($"CFA{n}");
        var sidB = await SeedOrgAsync($"CFB{n}");
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var owner = await factory.SignedInClientAsync($"c6-owner2-{n}");
        var admin = await factory.SignedInClientAsync($"c6-admin-{n}", isAdmin: true);
        (await owner.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 2, rsiRankLabel = "Director" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var guild = await GuildAsync(guildId);
        guild.OrgSid.Should().Be(sidB);
        guild.OrgMappedByUsername.Should().Be($"c6-admin-{n}");
        (await RoleAsync(guildId, roleId)).RankOrder.Should().Be(2);
    }

    [Fact]
    public async Task AnUnknownSid_Returns400_AndLeavesTheGuildUnmapped()
    {
        var n = Next();
        var (guildId, _, _) = await IngestGuildAsync(n);
        var client = await factory.SignedInClientAsync($"c6-badsid-{n}");

        var response = await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = $"NOPE{n}" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GuildAsync(guildId)).OrgSid.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownGuildOrRole_Returns404()
    {
        var n = Next();
        var sid = await SeedOrgAsync($"CFA{n}");
        var (guildId, _, _) = await IngestGuildAsync(n);
        var client = await factory.SignedInClientAsync($"c6-404-{n}");
        var body = new { isRank = true, rankOrder = 1, rsiRankLabel = (string?)null };

        (await client.PutAsJsonAsync(OrgUrl(DiscordTestKit.NewSnowflake()), new { orgSid = sid }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(RoleUrl(DiscordTestKit.NewSnowflake(), DiscordTestKit.NewSnowflake()), body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(RoleUrl(guildId, DiscordTestKit.NewSnowflake()), body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARoleBecomingARank_GetsItsPositionAsRankOrder()
    {
        var n = Next();
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var client = await factory.SignedInClientAsync($"c6-rank-{n}");

        var response = await client.PutAsJsonAsync(RoleUrl(guildId, roleId),
            new { isRank = true, rankOrder = (int?)null, rsiRankLabel = "  Officer  " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var role = await response.Content.ReadFromJsonAsync<JsonElement>();
        role.GetProperty("roleId").GetString().Should().Be(roleId);
        role.GetProperty("isRank").GetBoolean().Should().BeTrue();
        role.GetProperty("rankOrder").GetInt32().Should().Be(7);
        role.GetProperty("rsiRankLabel").GetString().Should().Be("Officer");
        var stored = await RoleAsync(guildId, roleId);
        (stored.IsRank, stored.RankOrder, stored.RsiRankLabel).Should().Be((true, (int?)7, (string?)"Officer"));

        // An explicit order is kept, and stays when a later edit leaves it out.
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 3, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = (int?)null, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        stored = await RoleAsync(guildId, roleId);
        (stored.IsRank, stored.RankOrder, stored.RsiRankLabel).Should().Be((true, (int?)3, (string?)null));

        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = -1, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = 1, rsiRankLabel = new string('x', 101) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Edits_AreWrittenToActivityLogs()
    {
        var n = Next();
        var sid = await SeedOrgAsync($"CFA{n}");
        var (guildId, roleId, _) = await IngestGuildAsync(n);
        var username = $"c6-audit-{n}";
        var client = await factory.SignedInClientAsync(username);

        (await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sid })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(RoleUrl(guildId, roleId), new { isRank = true, rankOrder = (int?)null, rsiRankLabel = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var userId = await ApiUserIdAsync(username);
        var roleEntityId = $"{guildId}:{roleId}";
        using var scope = factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.EntityId == guildId || l.EntityId == roleEntityId)
            .Select(l => new { l.Action, l.EntityType, l.EntityId, l.ApiUserId })
            .ToListAsync();
        logs.Should().ContainEquivalentOf(new
        {
            Action = "discord_map_org", EntityType = (string?)"discord_guild", EntityId = (string?)guildId, ApiUserId = (long?)userId,
        });
        logs.Should().ContainEquivalentOf(new
        {
            Action = "discord_update_role", EntityType = (string?)"discord_role", EntityId = (string?)roleEntityId, ApiUserId = (long?)userId,
        });
    }

    [Fact]
    public async Task ARemappedGuild_IsReconciledAgainstItsNewOrg()
    {
        var n = Next();
        var handleA = $"RemapA{n}";
        var handleB = $"RemapB{n}";
        var sidA = await SeedOrgAsync($"CFA{n}", (handleA, Cid(n, 1)));
        var sidB = await SeedOrgAsync($"CFB{n}", (handleB, Cid(n, 2)));
        var (guildId, _, userId) = await IngestGuildAsync(n);
        await SeedLinkAsync(userId, Cid(n, 1), handleA);
        var client = await factory.SignedInClientAsync($"c6-remap-{n}");

        (await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidA })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OrgGuildIdsAsync(client, sidA)).Should().Contain(guildId);
        (await OrgGuildIdsAsync(client, sidB)).Should().NotContain(guildId);
        (await ReconciliationAsync(client, guildId, userId)).Should().Be("ok");
        var before = await DiscrepanciesAsync(client, guildId);
        before.GetProperty("orgSid").GetString().Should().Be(sidA);
        Items(before).Should().NotContain(i => i.GetProperty("discordUserId").GetString() == userId);

        (await client.PutAsJsonAsync(OrgUrl(guildId), new { orgSid = sidB })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OrgGuildIdsAsync(client, sidA)).Should().NotContain(guildId);
        (await OrgGuildIdsAsync(client, sidB)).Should().Contain(guildId);
        (await ReconciliationAsync(client, guildId, userId)).Should().Be("not_in_rsi_org");
        var after = await DiscrepanciesAsync(client, guildId);
        after.GetProperty("orgSid").GetString().Should().Be(sidB);
        Items(after).Should().Contain(i =>
            Kind(i) == "not_in_rsi_org" && i.GetProperty("discordUserId").GetString() == userId);
        Items(after).Should().Contain(i => Kind(i) == "rsi_only" && i.GetProperty("handle").GetString() == handleB);
        Items(after).Should().NotContain(i => Kind(i) == "rsi_only" && i.GetProperty("handle").GetString() == handleA);
    }

    private static IEnumerable<JsonElement> Items(JsonElement discrepancies)
        => discrepancies.GetProperty("items").EnumerateArray().ToList();

    private static string? Kind(JsonElement item) => item.GetProperty("kind").GetString();

    private static async Task<List<string?>> OrgGuildIdsAsync(HttpClient client, string sid)
        => (await client.GetFromJsonAsync<JsonElement>($"/api/organizations/{sid}/discord"))
            .EnumerateArray().Select(g => g.GetProperty("guildId").GetString()).ToList();

    private static async Task<string?> ReconciliationAsync(HttpClient client, string guildId, string userId)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/discord/guilds/{guildId}/members?status=active&page=1&pageSize=50");
        return page.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("discordUserId").GetString() == userId)
            .GetProperty("reconciliation").GetString();
    }

    private static Task<JsonElement> DiscrepanciesAsync(HttpClient client, string guildId)
        => client.GetFromJsonAsync<JsonElement>($"/api/discord/guilds/{guildId}/discrepancies");

    /// <summary>A guild received through the real ingest: one member holding one non-hoisted role at position 7.</summary>
    private async Task<(string GuildId, string RoleId, string UserId)> IngestGuildAsync(int n)
    {
        var (ingest, _, _) = await DiscordTestKit.IngestClientAsync(factory, $"c6-ingest-{n}");
        var guildId = DiscordTestKit.NewSnowflake();
        var roleId = DiscordTestKit.NewSnowflake();
        var userId = DiscordTestKit.NewSnowflake();
        var sync = DiscordTestKit.Sync(guildId,
            [DiscordTestKit.Member(userId, $"c6member{n}", roleIds: [roleId])],
            roles: [DiscordTestKit.Role(roleId, "Officier", 7, hoist: false)]);
        (await DiscordTestKit.PostSyncAsync(ingest, guildId, sync)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (guildId, roleId, userId);
    }

    private async Task<string> SeedOrgAsync(string sid, params (string Handle, int CitizenId)[] roster)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var now = DateTime.UtcNow;
        db.Organizations.Add(new Organization { Sid = sid, Name = $"Org {sid}", Timestamp = now });
        foreach (var (handle, citizenId) in roster)
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Timestamp = now, IsActive = true,
            });
        await db.SaveChangesAsync();
        return sid;
    }

    private async Task SeedLinkAsync(string discordUserId, int citizenId, string handle)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var now = DateTime.UtcNow;
        var entity = new TrackedEntity { CitizenId = citizenId, CurrentHandle = handle, CreatedAt = now, UpdatedAt = now };
        db.TrackedEntities.Add(entity);
        await db.SaveChangesAsync();
        db.EntityLinks.Add(new EntityLink
        {
            TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = discordUserId,
            AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<DiscordGuild> GuildAsync(string guildId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>()
            .DiscordGuilds.AsNoTracking().SingleAsync(g => g.GuildId == guildId);
    }

    private async Task<DiscordRole> RoleAsync(string guildId, string roleId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>()
            .DiscordRoles.AsNoTracking().SingleAsync(r => r.GuildId == guildId && r.RoleId == roleId);
    }

    private async Task<long> ApiUserIdAsync(string username)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>()
            .ApiUsers.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }
}
