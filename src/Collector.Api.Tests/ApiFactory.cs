using System.Net.Http.Json;
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

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "sc-tracker-api-tests", Guid.NewGuid().ToString("N"));

    public ApiFactory()
    {
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, _dataDir);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__JwtSecret", "test-jwt-secret-0123456789abcdef0123456789");
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__AdminApiKey", AdminApiKey);
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

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
