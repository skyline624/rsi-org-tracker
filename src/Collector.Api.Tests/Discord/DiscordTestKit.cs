using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Dtos.Discord;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Builders shared by the Discord API tests: a client carrying only a discord:ingest key,
/// unique snowflakes, and sync bodies with sensible defaults.
/// </summary>
public static class DiscordTestKit
{
    private static long _lastSnowflake = 100_000_000_000_000_000;

    /// <summary>Creates a user, signs in, creates a discord:ingest key (180 days) and returns a client carrying only x-api-key.</summary>
    public static async Task<(HttpClient Client, string RawKey, long UserId)> IngestClientAsync(ApiFactory f, string username)
    {
        using var owner = await f.SignedInClientAsync(username);
        var me = await owner.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var created = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = $"Vencord {username}",
            expiresAt = DateTime.UtcNow.AddDays(180),
            scope = "discord:ingest",
        });
        created.EnsureSuccessStatusCode();
        var rawKey = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!;

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        return (client, rawKey, me.GetProperty("id").GetInt64());
    }

    /// <summary>Unique 18-digit string per call (thread-safe counter).</summary>
    public static string NewSnowflake() =>
        Interlocked.Increment(ref _lastSnowflake).ToString(CultureInfo.InvariantCulture);

    /// <summary>A sync of <paramref name="guildId"/>; <paramref name="expectedCount"/> defaults to the number of members.</summary>
    public static DiscordSyncRequest Sync(string guildId, IEnumerable<DiscordSyncMember> members,
        IEnumerable<DiscordSyncRole>? roles = null, bool complete = true, string method = "member-search",
        long durationMs = 1000, int? expectedCount = null)
    {
        var memberList = members.ToList();
        return new DiscordSyncRequest(
            PluginVersion: "1.0.0",
            CollectedAt: DateTimeOffset.UtcNow,
            CollectionDurationMs: durationMs,
            Guild: new DiscordSyncGuild(guildId, $"Guild {guildId}", null, memberList.Count),
            Coverage: new DiscordSyncCoverage(method, complete, expectedCount ?? memberList.Count, memberList.Count),
            Roles: roles?.ToList() ?? [],
            Members: memberList);
    }

    public static DiscordSyncMember Member(string userId, string username, IEnumerable<string>? roleIds = null,
        string? nick = null, string? globalName = null, DateTimeOffset? joinedAt = null, bool bot = false) =>
        new(userId, username, globalName, nick, roleIds?.ToList() ?? [], joinedAt, bot);

    public static DiscordSyncRole Role(string roleId, string name, int position, bool hoist = true, bool managed = false) =>
        new(roleId, name, position, "#e67e22", hoist, managed);

    public static Task<HttpResponseMessage> PostSyncAsync(HttpClient ingestClient, string guildId, DiscordSyncRequest body) =>
        ingestClient.PostAsJsonAsync($"/api/ingest/discord/guilds/{guildId}/syncs", body);
}
