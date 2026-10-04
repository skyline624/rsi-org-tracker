using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>An administrator creates the bot account's scoped key (spec § 5.1).</summary>
[Collection(ApiCollection.Name)]
public class AdminScopedKeyTests(ApiFactory factory)
{
    private async Task<long> NewAccountAsync()
    {
        var username = $"bot-account-{Guid.NewGuid():N}";
        await factory.CreateAccountAsync(username, "correct horse battery");
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ApiUsers
            .Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private static object BotKeyRequest(string? scope = ApiKeyScopes.BotRead)
        => new { name = "liberastra-bot", expiresAt = DateTime.UtcNow.AddDays(365), scope };

    [Fact]
    public async Task AnAdmin_CreatesAScopedKey_ForAnotherAccount()
    {
        var botId = await NewAccountAsync();
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var rawKey = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();
        (await factory.CreateClient().SendAsync(Get("/api/bot/search?q=ab", rawKey, "recherche"))).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AFullKey_CannotBeCreated_ForAnotherAccount()
    {
        var botId = await NewAccountAsync();
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        (await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest(scope: null))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ANonAdmin_IsForbidden()
    {
        var botId = await NewAccountAsync();
        var user = await factory.SignedInClientAsync($"user-{Guid.NewGuid():N}");

        (await user.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnUnknownAccount_IsA404()
    {
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        (await admin.PostAsJsonAsync("/api/admin/users/999999999/api-keys", BotKeyRequest())).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }
}
