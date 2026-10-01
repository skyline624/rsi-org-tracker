using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The DiscordIngestKey scheme, the Smart selector that sends the ingest path to it, and the
/// DiscordIngest policy (spec § 6.2). The access matrix on the real ingest route is tested
/// with its controller.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestKeyAuthTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";
    private const string IngestPath = "/api/ingest/discord/guilds/123456789012345678/syncs";

    private static string NewUsername() => $"ingest-auth-{Guid.NewGuid():N}";

    /// <summary>Runs one scheme's authentication on a request, as the pipeline would.</summary>
    private async Task<AuthenticateResult> AuthenticateAsync(
        string scheme, string path, string? apiKey = null, string? bearer = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        if (apiKey is not null) context.Request.Headers["x-api-key"] = apiKey;
        if (bearer is not null) context.Request.Headers["Authorization"] = $"Bearer {bearer}";
        return await context.AuthenticateAsync(scheme);
    }

    private static async Task<(long Id, string RawKey)> CreateKeyAsync(HttpClient owner, string? scope)
    {
        var response = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = "ingest-auth",
            expiresAt = scope is null ? (DateTime?)null : DateTime.UtcNow.AddDays(30),
            scope,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetInt64(), body.GetProperty("rawKey").GetString()!);
    }

    private async Task WithDbAsync(Func<ApiDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }

    private async Task<long> UserIdAsync(string username)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        return await db.ApiUsers.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    [Fact]
    public async Task IngestKey_IsAuthenticated_WithIdNameAndScopeOnly_EvenForAnAdminOwner()
    {
        var username = NewUsername();
        var owner = await factory.SignedInClientAsync(username, isAdmin: true);
        var (_, rawKey) = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);
        var userId = await UserIdAsync(username);

        var result = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: rawKey);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        var principal = result.Principal!;
        principal.Identity!.AuthenticationType.Should().Be(DiscordIngestAuth.SchemeName);
        principal.Claims.Select(c => c.Type).Should().BeEquivalentTo(
            ClaimTypes.NameIdentifier, ClaimTypes.Name, DiscordIngestAuth.ScopeClaimType);
        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(userId.ToString());
        principal.FindFirst(ClaimTypes.Name)!.Value.Should().Be(username);
        principal.FindFirst(DiscordIngestAuth.ScopeClaimType)!.Value.Should().Be("discord:ingest");
        principal.IsInRole("Admin").Should().BeFalse("an ingest key never carries its owner's admin role");
    }

    [Fact]
    public async Task FullKey_IsRefused()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());
        var (_, rawKey) = await CreateKeyAsync(owner, scope: null);

        var result = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: rawKey);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Not a discord:ingest key");
    }

    [Fact]
    public async Task StaticAdminKey_IsRefused()
    {
        // The static admin key is not an api.db key: the lookup finds nothing.
        var result = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: ApiFactory.AdminApiKey);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Invalid API key");
    }

    [Fact]
    public async Task RevokedOrExpiredIngestKey_IsRefused()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());
        var (revokedId, revoked) = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);
        var (expiredId, expired) = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);
        (await owner.DeleteAsync($"/api/api-keys/{revokedId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await WithDbAsync(db => db.ApiKeys.Where(k => k.Id == expiredId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, (DateTime?)DateTime.UtcNow.AddMinutes(-1))));

        var revokedResult = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: revoked);
        var expiredResult = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: expired);

        revokedResult.Succeeded.Should().BeFalse();
        revokedResult.Failure!.Message.Should().Be("Invalid API key");
        expiredResult.Succeeded.Should().BeFalse();
        expiredResult.Failure!.Message.Should().Be("Invalid API key");
    }

    [Fact]
    public async Task IngestKeyOfABannedOwner_IsRefused()
    {
        var username = NewUsername();
        var owner = await factory.SignedInClientAsync(username);
        var (_, rawKey) = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);
        await WithDbAsync(db => db.ApiUsers.Where(u => u.Username == username)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, true)));

        var result = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: rawKey);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Account is banned");
    }

    [Fact]
    public async Task NoKey_IsNoResult()
    {
        var result = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath);

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task SmartScheme_SendsTheIngestPathToTheIngestScheme_WhateverTheHeaders()
    {
        var username = NewUsername();
        var owner = await factory.SignedInClientAsync(username);
        var (_, ingestKey) = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);
        var (_, fullKey) = await CreateKeyAsync(owner, scope: null);
        var jwt = (await factory.LoginAsync(username, Password)).AccessToken;

        var ingest = await AuthenticateAsync("Smart", IngestPath, apiKey: ingestKey);
        ingest.Succeeded.Should().BeTrue(ingest.Failure?.Message);
        ingest.Principal!.Identity!.AuthenticationType.Should().Be(DiscordIngestAuth.SchemeName);
        (await AuthenticateAsync("Smart", "/API/Ingest/Discord/guilds/1/syncs", apiKey: ingestKey))
            .Succeeded.Should().BeTrue("the path prefix is matched regardless of case");
        (await AuthenticateAsync("Smart", IngestPath, apiKey: ingestKey, bearer: jwt))
            .Succeeded.Should().BeTrue("an Authorization header does not divert the ingest path to JWT");
        (await AuthenticateAsync("Smart", IngestPath, bearer: jwt))
            .None.Should().BeTrue("the ingest scheme never reads the Authorization header, so a JWT alone is no credential");
        (await AuthenticateAsync("Smart", IngestPath, apiKey: fullKey)).Succeeded.Should().BeFalse();
        (await AuthenticateAsync("Smart", IngestPath, apiKey: ApiFactory.AdminApiKey)).Succeeded.Should().BeFalse();

        var elsewhere = await AuthenticateAsync("Smart", "/api/auth/me", apiKey: ingestKey);
        elsewhere.Succeeded.Should().BeFalse();
        elsewhere.Failure!.Message.Should().Be("Scoped key");
        (await AuthenticateAsync("Smart", "/api/auth/me", bearer: jwt))
            .Succeeded.Should().BeTrue("JWT still works off the ingest path");
    }

    [Fact]
    public async Task SmartScheme_DoesNotTreatASimilarPathAsAnIngestRoute()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());
        var (_, ingestKey) = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);

        var result = await AuthenticateAsync("Smart", "/api/ingest/discord-other/guilds/1/syncs", apiKey: ingestKey);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be("Scoped key", "the prefix must end at a path segment boundary");
    }

    [Fact]
    public async Task QueryStringKey_IsIgnored_AndAnUnknownHeaderKeyIsRefused()
    {
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Path = IngestPath;
        context.Request.QueryString = new QueryString($"?api_key={ApiFactory.AdminApiKey}");

        (await context.AuthenticateAsync(DiscordIngestAuth.SchemeName)).None.Should().BeTrue();
        var unknown = await AuthenticateAsync(DiscordIngestAuth.SchemeName, IngestPath, apiKey: "unknown-key");
        unknown.Succeeded.Should().BeFalse();
        unknown.Failure!.Message.Should().Be("Invalid API key");
    }

    [Fact]
    public async Task DiscordIngestPolicy_UsesTheIngestScheme_AndRequiresTheScopeClaim()
    {
        static ClaimsPrincipal Principal(string? authenticationType, params Claim[] claims) =>
            new(new ClaimsIdentity(claims, authenticationType));
        using var scope = factory.Services.CreateScope();
        var policy = await scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(DiscordIngestAuth.PolicyName);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var scopeClaim = new Claim(DiscordIngestAuth.ScopeClaimType, DiscordIngestAuth.IngestScope);

        policy.Should().NotBeNull();
        policy!.AuthenticationSchemes.Should().Equal(DiscordIngestAuth.SchemeName);
        (await authorization.AuthorizeAsync(
                Principal(DiscordIngestAuth.SchemeName, new Claim(ClaimTypes.NameIdentifier, "7"), scopeClaim),
                null, DiscordIngestAuth.PolicyName))
            .Succeeded.Should().BeTrue();
        (await authorization.AuthorizeAsync(
                Principal("ApiKey", new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "Admin")),
                null, DiscordIngestAuth.PolicyName))
            .Succeeded.Should().BeFalse("a full key, even an admin's, lacks the scope claim");
        (await authorization.AuthorizeAsync(Principal(null, scopeClaim), null, DiscordIngestAuth.PolicyName))
            .Succeeded.Should().BeFalse("the scope claim alone does not make an authenticated user");
    }
}
