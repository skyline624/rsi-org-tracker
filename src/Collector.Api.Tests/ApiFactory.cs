using System.Net.Http.Json;
using System.Security.Cryptography;
using Collector.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>
/// Boots the real API (Program.cs) against a throw-away data directory.
///
/// Program.cs reads its secrets and data directory while building the host, before
/// WebApplicationFactory configuration hooks run, so they are passed through the
/// same environment variables production uses. Tests sharing this factory run in
/// one xUnit collection, never in parallel.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminApiKey = "test-admin-key-0123456789abcdef";

    /// <summary>Legacy HS256 secret: still configured, must no longer be accepted.</summary>
    public const string LegacyJwtSecret = "test-jwt-secret-0123456789abcdef0123456789";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "sc-tracker-api-tests", Guid.NewGuid().ToString("N"));

    public ApiFactory()
    {
        Directory.CreateDirectory(_dataDir);
        var keyPath = Path.Combine(_dataDir, "jwt-private.pem");
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, _dataDir);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__JwtSecret", LegacyJwtSecret);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__Jwt__PrivateKeyPath", keyPath);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__AdminApiKey", AdminApiKey);

        // Every test client shares one partition (no peer address in TestServer); budgets
        // are exercised by dedicated tests with their own limits.
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__UserPermitLimit", "100000");
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__AnonymousPermitLimit", "100000");
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__Login__PermitLimit", "100000");
    }

    /// <summary>Logs in and returns the raw auth response body.</summary>
    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResult>())!;
    }

    /// <summary>Creates an account through the admin API (authenticated with the admin key).</summary>
    public async Task CreateAccountAsync(string username, string password, bool isAdmin = false)
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", AdminApiKey);
        var response = await client.PostAsJsonAsync("/api/admin/users", new
        {
            username,
            email = $"{username}@example.test",
            password,
            isAdmin,
        });
        response.EnsureSuccessStatusCode();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { /* SQLite handles may linger */ }
    }
}

public sealed record LoginResult(string AccessToken, string RefreshToken, DateTime ExpiresAt);

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
