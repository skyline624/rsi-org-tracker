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
    private const string AuditAction = "admin_create_scoped_key";

    private async Task<long> NewAccountAsync()
    {
        var username = $"bot-account-{Guid.NewGuid():N}";
        await factory.CreateAccountAsync(username, "correct horse battery");
        return await IdOfAsync(username);
    }

    private async Task<long> IdOfAsync(string username)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ApiUsers
            .Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private async Task<T> InDbAsync<T>(Func<ApiDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }

    private HttpClient StaticAdminKeyClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", ApiFactory.AdminApiKey);
        return client;
    }

    private static object BotKeyRequest(string? scope = ApiKeyScopes.BotRead, int days = 365)
        => new { name = "liberastra-bot", expiresAt = DateTime.UtcNow.AddDays(days), scope };

    [Fact]
    public async Task AnAdmin_CreatesAScopedKey_ForAnotherAccount()
    {
        var botId = await NewAccountAsync();
        var adminName = $"admin-{Guid.NewGuid():N}";
        var admin = await factory.SignedInClientAsync(adminName, isAdmin: true);
        var adminId = await IdOfAsync(adminName);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rawKey = body.GetProperty("rawKey").GetString();
        (await factory.CreateClient().SendAsync(Get("/api/bot/search?q=ab", rawKey, "recherche"))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // The key belongs to the other account, not to the administrator who created it.
        var keyId = body.GetProperty("id").GetInt64();
        var stored = await InDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == keyId));
        stored.ApiUserId.Should().Be(botId);
        stored.Scope.Should().Be(ApiKeyScopes.BotRead);

        var audit = await InDbAsync(db => db.ActivityLogs.AsNoTracking()
            .SingleAsync(l => l.Action == AuditAction && l.EntityId == botId.ToString()));
        audit.ApiUserId.Should().Be(adminId);
        audit.EntityType.Should().Be("user");
    }

    [Fact]
    public async Task TheStaticAdminKey_CreatesAScopedKey_AndTheAuditEntryHasNoUser()
    {
        var botId = await NewAccountAsync();

        var response = await StaticAdminKeyClient()
            .PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest());

        // The static key has no api_users row: its audit entry is written without a user and
        // must not turn an already-persisted key into a 500.
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var rawKey = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();
        (await factory.CreateClient().SendAsync(Get("/api/bot/search?q=ab", rawKey, "recherche"))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var audit = await InDbAsync(db => db.ActivityLogs.AsNoTracking()
            .SingleAsync(l => l.Action == AuditAction && l.EntityId == botId.ToString()));
        audit.ApiUserId.Should().BeNull();
        audit.EntityType.Should().Be("user");
    }

    [Fact]
    public async Task AFullKey_CannotBeCreated_ForAnotherAccount()
    {
        var botId = await NewAccountAsync();
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        (await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest(scope: null))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await InDbAsync(db => db.ApiKeys.CountAsync(k => k.ApiUserId == botId))).Should().Be(0);
    }

    [Fact]
    public async Task AScopedKey_CannotOutliveTheLifetimeCap_WhenAnAdminCreatesIt()
    {
        var botId = await NewAccountAsync();
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest(days: 400));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
            .Should().Contain("365");
        (await InDbAsync(db => db.ApiKeys.CountAsync(k => k.ApiUserId == botId))).Should().Be(0);
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

        var response = await admin.PostAsJsonAsync("/api/admin/users/999999999/api-keys", BotKeyRequest());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // A missing route is also a 404; only the handler's own answer names the account.
        (await response.Content.ReadAsStringAsync()).Should().Contain("introuvable");
    }
}
