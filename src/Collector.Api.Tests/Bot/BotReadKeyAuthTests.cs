using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Bot;

/// <summary>The BotReadKey scheme and the Smart selector sending /api/bot to it (spec § 5.1).</summary>
[Collection(ApiCollection.Name)]
public class BotReadKeyAuthTests(ApiFactory factory)
{
    private const string BotPath = "/api/bot/search";

    private async Task<AuthenticateResult> AuthenticateAsync(string scheme, string path, string? apiKey)
    {
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        if (apiKey is not null) context.Request.Headers["x-api-key"] = apiKey;
        return await context.AuthenticateAsync(scheme);
    }

    private static async Task<(long Id, string RawKey)> CreateKeyWithIdAsync(HttpClient owner, string? scope, int days = 30)
    {
        var response = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = "bot-auth",
            expiresAt = scope is null ? (DateTime?)null : DateTime.UtcNow.AddDays(days),
            scope,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetInt64(), body.GetProperty("rawKey").GetString()!);
    }

    private static async Task<string> CreateKeyAsync(HttpClient owner, string? scope, int days = 30) =>
        (await CreateKeyWithIdAsync(owner, scope, days)).RawKey;

    private async Task WithDbAsync(Func<ApiDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }

    private Task<HttpClient> OwnerAsync() => factory.SignedInClientAsync($"bot-auth-{Guid.NewGuid():N}");

    [Fact]
    public async Task BotKey_IsAuthenticated_OnTheBotScheme_WithItsScope()
    {
        var key = await CreateKeyAsync(await OwnerAsync(), ApiKeyScopes.BotRead);

        var result = await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, key);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirstValue(BotReadAuth.ScopeClaimType).Should().Be(ApiKeyScopes.BotRead);
        result.Principal!.IsInRole("Admin").Should().BeFalse();
    }

    [Fact]
    public async Task RevokedOrExpiredBotKey_IsRefused()
    {
        var owner = await OwnerAsync();
        var (revokedId, revoked) = await CreateKeyWithIdAsync(owner, ApiKeyScopes.BotRead);
        var (expiredId, expired) = await CreateKeyWithIdAsync(owner, ApiKeyScopes.BotRead);
        (await owner.DeleteAsync($"/api/api-keys/{revokedId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await WithDbAsync(db => db.ApiKeys.Where(k => k.Id == expiredId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, (DateTime?)DateTime.UtcNow.AddMinutes(-1))));

        var revokedResult = await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, revoked);
        var expiredResult = await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, expired);

        revokedResult.Succeeded.Should().BeFalse();
        revokedResult.Failure!.Message.Should().Be("Invalid API key");
        expiredResult.Succeeded.Should().BeFalse();
        expiredResult.Failure!.Message.Should().Be("Invalid API key");
    }

    [Fact]
    public async Task BotKeyOfABannedOwner_IsRefused()
    {
        var username = $"bot-auth-{Guid.NewGuid():N}";
        var owner = await factory.SignedInClientAsync(username);
        var key = await CreateKeyAsync(owner, ApiKeyScopes.BotRead);
        await WithDbAsync(db => db.ApiUsers.Where(u => u.Username == username)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, true)));

        var result = await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, key);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Account is banned");
    }

    [Fact]
    public async Task NoKey_IsNoResult_ByTheBotScheme()
    {
        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, apiKey: null)).None.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApiKeyScopes.DiscordIngest)]
    public async Task OtherKeys_AreRefused_ByTheBotScheme(string? scope)
    {
        var key = await CreateKeyAsync(await OwnerAsync(), scope);

        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, key)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TheAdminKey_IsRefused_ByTheBotScheme()
    {
        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, ApiFactory.AdminApiKey)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BotKey_IsRefused_ByTheGeneralScheme()
    {
        var key = await CreateKeyAsync(await OwnerAsync(), ApiKeyScopes.BotRead);

        (await AuthenticateAsync("ApiKey", "/api/organizations", key)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BotKey_IsRefused_ByTheDiscordIngestScheme_AndTheReverse()
    {
        var owner = await OwnerAsync();
        var botKey = await CreateKeyAsync(owner, ApiKeyScopes.BotRead);
        var ingestKey = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);

        (await AuthenticateAsync(DiscordIngestAuth.SchemeName, "/api/ingest/discord/roster", botKey)).Succeeded.Should().BeFalse();
        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, ingestKey)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TheSmartScheme_SendsTheBotPath_ToTheBotScheme()
    {
        var key = await CreateKeyAsync(await OwnerAsync(), ApiKeyScopes.BotRead);

        var result = await AuthenticateAsync("Smart", BotPath, key);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirstValue(BotReadAuth.ScopeClaimType).Should().Be(ApiKeyScopes.BotRead);
    }

    [Theory]
    [InlineData(400)]
    public async Task ABotKey_MustExpireWithinAYear(int days)
    {
        var owner = await OwnerAsync();

        var tooLong = await owner.PostAsJsonAsync("/api/api-keys",
            new { name = "bot", expiresAt = DateTime.UtcNow.AddDays(days), scope = ApiKeyScopes.BotRead });
        var noExpiry = await owner.PostAsJsonAsync("/api/api-keys",
            new { name = "bot", expiresAt = (DateTime?)null, scope = ApiKeyScopes.BotRead });

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noExpiry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
