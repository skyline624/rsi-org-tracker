using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Models;
using Collector.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Collector.Api.Tests.Bot;

internal static class BotTestSupport
{
    public const string DiscordUser = "123456789012345678";

    public static string NewSid() => "B" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static string NewHandle() => "p" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>A bot:read key owned by a fresh, non-admin account.</summary>
    public static async Task<string> CreateBotKeyAsync(ApiFactory factory)
    {
        var owner = await factory.SignedInClientAsync($"bot-{Guid.NewGuid():N}");
        var response = await owner.PostAsJsonAsync("/api/api-keys",
            new { name = "bot", expiresAt = DateTime.UtcNow.AddDays(30), scope = ApiKeyScopes.BotRead });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!;
    }

    public static HttpRequestMessage Get(string url, string? apiKey, string command, string? discordUser = DiscordUser)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        if (discordUser is not null) request.Headers.Add("X-Discord-User", discordUser);
        request.Headers.Add("X-Bot-Command", command);
        return request;
    }

    public static async Task SeedAsync(ApiFactory factory, Action<TrackerDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}
