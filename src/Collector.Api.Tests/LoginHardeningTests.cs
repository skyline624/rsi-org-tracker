using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Auth;
using Collector.Api.Data;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class LoginHardeningTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";

    private sealed class RecordingHasher : IPasswordHasher
    {
        private readonly BCryptPasswordHasher _inner = new();
        public List<string> VerifiedHashes { get; } = [];
        public string Hash(string password) => _inner.Hash(password);
        public bool Verify(string password, string hash)
        {
            VerifiedHashes.Add(hash);
            return _inner.Verify(password, hash);
        }
    }

    private async Task<(HttpStatusCode Status, string? Detail)> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (response.IsSuccessStatusCode) return (response.StatusCode, null);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, body.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null);
    }

    private async Task WithDbAsync(Func<ApiDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }

    [Fact]
    public async Task UnknownUser_StillPaysForAPasswordCheck()
    {
        // Same work as a wrong password, so response time does not reveal which accounts exist.
        var hasher = new RecordingHasher();
        using var app = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher>(hasher)));

        var (status, _) = await LoginAsync(app.CreateClient(), "no-such-user", "whatever");

        status.Should().Be(HttpStatusCode.Unauthorized);
        hasher.VerifiedHashes.Should().ContainSingle().Which.Should().StartWith("$2");
    }

    [Fact]
    public async Task FiveFailures_LockTheAccount_EvenForTheRightPassword()
    {
        await factory.CreateAccountAsync("lock-me", Password);
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            (await LoginAsync(client, "lock-me", "wrong")).Status.Should().Be(HttpStatusCode.Unauthorized);
        }

        var (status, detail) = await LoginAsync(client, "lock-me", Password);

        status.Should().Be(HttpStatusCode.Unauthorized);
        detail.Should().Be("Invalid username or password", "a lock must not reveal that the account exists");
    }

    [Fact]
    public async Task ExpiredLock_LetsTheRightPasswordIn_AndResetsTheCounter()
    {
        await factory.CreateAccountAsync("lock-expired", Password);
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++) await LoginAsync(client, "lock-expired", "wrong");
        await WithDbAsync(db => db.ApiUsers.Where(u => u.Username == "lock-expired")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTime.UtcNow.AddSeconds(-1))));

        (await LoginAsync(client, "lock-expired", Password)).Status.Should().Be(HttpStatusCode.OK);

        await WithDbAsync(async db =>
            (await db.ApiUsers.SingleAsync(u => u.Username == "lock-expired")).FailedLoginCount.Should().Be(0));
    }

    [Fact]
    public async Task BannedAccount_IsOnlyRevealedAfterTheRightPassword()
    {
        await factory.CreateAccountAsync("banned-user", Password);
        await WithDbAsync(db => db.ApiUsers.Where(u => u.Username == "banned-user")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBanned, true)));
        var client = factory.CreateClient();

        (await LoginAsync(client, "banned-user", "wrong")).Detail.Should().Be("Invalid username or password");
        (await LoginAsync(client, "banned-user", Password)).Detail.Should().Be("Account is banned");
    }

    [Fact]
    public async Task FailedLogin_IsRecordedInTheActivityLog()
    {
        await factory.CreateAccountAsync("audit-me", Password);

        await LoginAsync(factory.CreateClient(), "audit-me", "wrong");

        await WithDbAsync(async db =>
        {
            var userId = await db.ApiUsers.Where(u => u.Username == "audit-me").Select(u => u.Id).SingleAsync();
            (await db.ActivityLogs.AnyAsync(l => l.Action == "login_failed" && l.ApiUserId == userId))
                .Should().BeTrue();
        });
    }
}
