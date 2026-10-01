using System.Net.Http.Json;
using System.Security.Cryptography;
using Collector.Data;
using Collector.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Plugin settings published by <c>GET api/discord/ingest-config</c> (api.env in production).</summary>
    public const string DiscordIngestPublicUrl = "https://203.0.113.10";

    /// <summary>A fingerprint in the openssl format (32 colon-separated bytes).</summary>
    public const string DiscordIngestCertificateSha256 =
        "0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0:0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "sc-tracker-api-tests", Guid.NewGuid().ToString("N"));

    public string DataDir => _dataDir;

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

        // tracker.db is migrated by the collector in production; give the API the same schema.
        using (var trackerDb = new ServiceCollection()
                   .AddDbContext<TrackerDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dataDir, "tracker.db")}"))
                   .BuildServiceProvider())
        {
            trackerDb.EnsureDatabaseAsync(_dataDir).GetAwaiter().GetResult();
        }

        Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, _dataDir);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__JwtSecret", LegacyJwtSecret);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__Jwt__PrivateKeyPath", keyPath);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__AdminApiKey", AdminApiKey);
        // Same variables as /etc/sc-tracker/api.env (deploy/README.md, "Rosters Discord").
        Environment.SetEnvironmentVariable("COLLECTOR_API_Discord__Ingest__PublicUrl", DiscordIngestPublicUrl);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Discord__Ingest__CertificateSha256", DiscordIngestCertificateSha256);
        // Automatic-link tests run a controlled worker; background passes must not race seeded suggestions.
        Environment.SetEnvironmentVariable("COLLECTOR_API_Discord__AutoLink__Enabled", "false");

        // Every test client shares one partition (no peer address in TestServer); budgets
        // are exercised by dedicated tests with their own limits.
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__UserPermitLimit", "100000");
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__AnonymousPermitLimit", "100000");
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__Login__PermitLimit", "100000");
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__RateLimit__DiscordIngest__PermitLimit", "100000");
    }

    /// <summary>An HTTP client authenticated as a freshly created account.</summary>
    public async Task<HttpClient> SignedInClientAsync(string username, bool isAdmin = false)
    {
        await CreateAccountAsync(username, "correct horse battery", isAdmin);
        var login = await LoginAsync(username, "correct horse battery");
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        return client;
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
