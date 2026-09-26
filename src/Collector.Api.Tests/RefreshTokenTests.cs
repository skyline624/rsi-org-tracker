using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Collector.Api.Data;
using Collector.Api.Models;
using Collector.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class RefreshTokenTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";

    private async Task<LoginResult> NewSessionAsync(string username)
    {
        await factory.CreateAccountAsync(username, Password);
        return await factory.LoginAsync(username, Password);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

    private async Task<LoginResult> RefreshOkAsync(string refreshToken)
    {
        var response = await RefreshAsync(refreshToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LoginResult>())!;
    }

    private async Task WithDbAsync(Func<ApiDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }

    [Fact]
    public async Task Refresh_RotatesTheToken()
    {
        var session = await NewSessionAsync("rt-rotate");

        var next = await RefreshOkAsync(session.RefreshToken);

        next.RefreshToken.Should().NotBe(session.RefreshToken);
        (await RefreshAsync(next.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RotatedTokenReplayedWithinTheGracePeriod_IsAccepted()
    {
        // Two tabs refreshing at the same time both present the same token.
        var session = await NewSessionAsync("rt-grace");
        await RefreshOkAsync(session.RefreshToken);

        (await RefreshAsync(session.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RotatedTokenReplayedAfterTheGracePeriod_RevokesTheWholeFamily()
    {
        var session = await NewSessionAsync("rt-reuse");
        var next = await RefreshOkAsync(session.RefreshToken);
        await WithDbAsync(db => db.RefreshTokens
            .Where(t => t.RevokedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow.AddMinutes(-2))));

        (await RefreshAsync(session.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(next.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BannedUser_CannotRefresh()
    {
        var session = await NewSessionAsync("rt-banned");
        await WithDbAsync(db => db.ApiUsers
            .Where(u => u.Username == "rt-banned")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, true)));

        (await RefreshAsync(session.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_RevokesEverySession_AndReturnsFreshTokens()
    {
        var current = await NewSessionAsync("rt-password");
        var otherDevice = await factory.LoginAsync("rt-password", Password);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);

        var response = await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = Password, newPassword = "a brand new passphrase" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fresh = (await response.Content.ReadFromJsonAsync<LoginResult>())!;
        fresh.RefreshToken.Should().NotBeNullOrEmpty();
        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(current.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(fresh.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ARotatedToken_ReplayedWithinTheGracePeriod_AfterLogout_IsRefused()
    {
        // Deferred from the final review: the 30 s grace for concurrent refreshes brought
        // a session back after its logout.
        var session = await NewSessionAsync("rt-grace-logout");
        var next = await RefreshOkAsync(session.RefreshToken);
        await factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new { refreshToken = next.RefreshToken });

        (await RefreshAsync(session.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARotatedToken_ReplayedWithinTheGracePeriod_AfterAPasswordChange_IsRefused()
    {
        var session = await NewSessionAsync("rt-grace-password");
        var next = await RefreshOkAsync(session.RefreshToken);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", next.AccessToken);
        (await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = Password, newPassword = "another correct horse" })).EnsureSuccessStatusCode();

        (await RefreshAsync(session.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_RevokesTheWholeFamily()
    {
        var session = await NewSessionAsync("rt-logout");
        var next = await RefreshOkAsync(session.RefreshToken);

        // Logging out with an older token of the same session still ends the session.
        await factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new { refreshToken = session.RefreshToken });

        (await RefreshAsync(next.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cleanup_DeletesTokensExpiredOrRevokedForMoreThanAWeek()
    {
        var now = DateTime.UtcNow;
        long userId = 0;
        await factory.CreateAccountAsync("rt-cleanup", Password);
        await WithDbAsync(async db =>
        {
            userId = await db.ApiUsers.Where(u => u.Username == "rt-cleanup").Select(u => u.Id).SingleAsync();
            RefreshToken Token(string hash, DateTime expires, DateTime? revokedAt) => new()
            {
                ApiUserId = userId, TokenHash = hash, CreatedAt = now.AddDays(-40), ExpiresAt = expires,
                IsRevoked = revokedAt != null, RevokedAt = revokedAt,
            };
            db.RefreshTokens.AddRange(
                Token("cleanup-active", now.AddDays(10), null),
                Token("cleanup-recently-revoked", now.AddDays(10), now.AddDays(-2)),
                Token("cleanup-long-revoked", now.AddDays(10), now.AddDays(-8)),
                Token("cleanup-long-expired", now.AddDays(-8), null));
            await db.SaveChangesAsync();
        });

        await WithDbAsync(db => RefreshTokenCleanupService.PurgeAsync(db, now, CancellationToken.None));

        await WithDbAsync(async db =>
        {
            var left = await db.RefreshTokens.Where(t => t.ApiUserId == userId).Select(t => t.TokenHash).ToListAsync();
            left.Should().BeEquivalentTo("cleanup-active", "cleanup-recently-revoked");
        });
    }
}
