using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Dtos.ApiKeys;
using Collector.Api.Models;
using Collector.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// API keys limited to a scope (spec § 6.2): the creation rules, the scope in the DTOs and
/// in the validation result, the prefix fix, and the ApiKey scheme refusing scoped keys.
/// </summary>
[Collection(ApiCollection.Name)]
public class ScopedApiKeyTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";

    private static string NewUsername() => $"apikey-scope-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> PostKeyAsync(HttpClient owner, string name, DateTime? expiresAt, string? scope) =>
        owner.PostAsJsonAsync("/api/api-keys", new { name, expiresAt, scope });

    private static async Task<JsonElement> CreateKeyAsync(HttpClient owner, string name, DateTime? expiresAt, string? scope)
    {
        var response = await PostKeyAsync(owner, name, expiresAt, scope);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListedKeyAsync(HttpClient owner, long id)
    {
        var keys = await owner.GetFromJsonAsync<JsonElement>("/api/api-keys");
        return keys.EnumerateArray().Single(k => k.GetProperty("id").GetInt64() == id);
    }

    private async Task<HttpStatusCode> MeWithKeyAsync(string rawKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", rawKey);
        return (await client.GetAsync("/api/auth/me")).StatusCode;
    }

    [Fact]
    public async Task KeyWithoutScope_IsAFullKey()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());

        var created = await CreateKeyAsync(owner, "full", null, null);

        created.GetProperty("scope").ValueKind.Should().Be(JsonValueKind.Null);
        (await ListedKeyAsync(owner, created.GetProperty("id").GetInt64()))
            .GetProperty("scope").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task IngestKey_ShowsItsScopeAndExpiry_OnCreationAndInTheList()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());
        var expiresAt = DateTime.UtcNow.AddDays(180);

        var created = await CreateKeyAsync(owner, "vencord", expiresAt, ApiKeyScopes.DiscordIngest);

        created.GetProperty("scope").GetString().Should().Be("discord:ingest");
        created.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(expiresAt, TimeSpan.FromSeconds(1));
        created.GetProperty("rawKey").GetString().Should().StartWith(created.GetProperty("keyPrefix").GetString() + "_");
        var listed = await ListedKeyAsync(owner, created.GetProperty("id").GetInt64());
        listed.GetProperty("scope").GetString().Should().Be("discord:ingest");
        listed.TryGetProperty("rawKey", out _).Should().BeFalse("the raw key is shown once, at creation");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData("DISCORD:INGEST")]
    public async Task UnknownScope_IsRejected_AndNothingIsCreated(string scope)
    {
        var owner = await factory.SignedInClientAsync(NewUsername());

        var response = await PostKeyAsync(owner, "bad-scope", DateTime.UtcNow.AddDays(30), scope);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.GetFromJsonAsync<JsonElement>("/api/api-keys")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task IngestKey_WithoutExpiry_IsRejected()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());

        (await PostKeyAsync(owner, "no-expiry", null, ApiKeyScopes.DiscordIngest))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task IngestKey_ExpiringInThePastOrBeyondAYear_IsRejected(int days)
    {
        var owner = await factory.SignedInClientAsync(NewUsername());

        (await PostKeyAsync(owner, "bad-expiry", DateTime.UtcNow.AddDays(days), ApiKeyScopes.DiscordIngest))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IngestKey_MayLastUpToAYear()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());

        // The server reads its clock after the test did, so this stays within its 365 days.
        await CreateKeyAsync(owner, "one-year", DateTime.UtcNow.AddDays(365).AddMinutes(-1), ApiKeyScopes.DiscordIngest);
    }

    [Fact]
    public async Task ExpiryWithAnOffset_IsCheckedAndReturnedAsUtc()
    {
        // System.Text.Json turns "+02:00" into a local DateTime: without the conversion the
        // creation answer carried a local offset and the 365-day check was off by hours.
        var owner = await factory.SignedInClientAsync(NewUsername());
        var instant = DateTimeOffset.UtcNow.AddDays(180);

        var response = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = "offset",
            expiresAt = instant.ToOffset(TimeSpan.FromHours(2)),
            scope = ApiKeyScopes.DiscordIngest,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        created.GetProperty("expiresAt").GetString().Should().EndWith("Z");
        created.GetProperty("expiresAt").GetDateTimeOffset().Should().BeCloseTo(instant, TimeSpan.FromSeconds(1));
        var listed = await ListedKeyAsync(owner, created.GetProperty("id").GetInt64());
        listed.GetProperty("expiresAt").GetDateTimeOffset().Should().BeCloseTo(instant, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task KeyPrefix_IsSixLettersOrDigits_OverTwoThousandCreations()
    {
        // The former prefix (base64 minus '+' and '/', cut to 6) threw on about 0.15 % of
        // creations: 2 000 creations hit it in about 95 % of runs. The transaction is never
        // committed, so none of these keys stays in the shared api.db.
        var username = NewUsername();
        await factory.CreateAccountAsync(username, Password);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var userId = await db.ApiUsers.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        for (var i = 0; i < 2000; i++)
        {
            var (rawKey, dto) = await keys.CreateAsync(userId, new CreateApiKeyRequest($"loop-{i}", null));
            dto.KeyPrefix.Should().MatchRegex("^[A-Za-z0-9]{6}$");
            rawKey.Should().StartWith(dto.KeyPrefix + "_");
            db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task Validation_ReturnsTheOwnerAndTheScope()
    {
        var username = NewUsername();
        var owner = await factory.SignedInClientAsync(username);
        var ingest = await CreateKeyAsync(owner, "ingest", DateTime.UtcNow.AddDays(30), ApiKeyScopes.DiscordIngest);
        var full = await CreateKeyAsync(owner, "full", null, null);
        using var scope = factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();

        var ingestValidation = await keys.ValidateAsync(ingest.GetProperty("rawKey").GetString()!);
        var fullValidation = await keys.ValidateAsync(full.GetProperty("rawKey").GetString()!);

        ingestValidation.Should().NotBeNull();
        ingestValidation!.User.Username.Should().Be(username);
        ingestValidation.Scope.Should().Be(ApiKeyScopes.DiscordIngest);
        fullValidation.Should().NotBeNull();
        fullValidation!.Scope.Should().BeNull();
        (await keys.ValidateAsync("nothing_like-a-key")).Should().BeNull();
    }

    [Fact]
    public async Task UsageTimestampWriteContention_DoesNotFailAValidCredential()
    {
        var username = NewUsername();
        var owner = await factory.SignedInClientAsync(username);
        var created = await CreateKeyAsync(owner, "contended-ingest", DateTime.UtcNow.AddDays(30), ApiKeyScopes.DiscordIngest);
        var id = created.GetProperty("id").GetInt64();
        var rawKey = created.GetProperty("rawKey").GetString()!;
        var connectionString = $"Data Source={Path.Combine(factory.DataDir, "api.db")};Pooling=False;Default Timeout=1";
        await using var locker = new SqliteConnection(connectionString);
        await locker.OpenAsync();
        await using (var begin = locker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }
        await using var db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(connectionString).Options);
        db.Database.SetCommandTimeout(1);

        var validated = await new ApiKeyService(db).ValidateAsync(rawKey);

        validated.Should().NotBeNull();
        validated!.User.Username.Should().Be(username);
        validated.Scope.Should().Be(ApiKeyScopes.DiscordIngest);
        db.ChangeTracker.Entries().Should().BeEmpty("usage telemetry is not tracked request state");
        await using (var rollback = locker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }
        (await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == id)).LastUsedAt.Should().BeNull();
        (await new ApiKeyService(db).ValidateAsync(rawKey)).Should().NotBeNull();
        (await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == id)).LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ScopedKey_IsRefusedByTheApiKeyScheme_WhileAFullKeyIsAccepted()
    {
        var owner = await factory.SignedInClientAsync(NewUsername());
        var ingest = await CreateKeyAsync(owner, "ingest", DateTime.UtcNow.AddDays(30), ApiKeyScopes.DiscordIngest);
        var full = await CreateKeyAsync(owner, "full", null, null);

        (await MeWithKeyAsync(ingest.GetProperty("rawKey").GetString()!)).Should().Be(HttpStatusCode.Unauthorized);
        (await MeWithKeyAsync(full.GetProperty("rawKey").GetString()!)).Should().Be(HttpStatusCode.OK);
    }
}
