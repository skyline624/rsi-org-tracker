using System.Security.Cryptography;
using Collector.Api.Data;
using Collector.Api.Dtos.ApiKeys;
using Collector.Api.Errors;
using Collector.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

/// <summary>The owner and capability of a live API key.</summary>
public sealed record ApiKeyValidation(ApiUser User, string? Scope);

/// <summary>Creates, validates and revokes hashed API credentials.</summary>
public class ApiKeyService
{
    private const string PrefixAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int PrefixLength = 6;

    private readonly ApiDbContext _db;

    public ApiKeyService(ApiDbContext db)
    {
        _db = db;
    }

    public async Task<(string RawKey, ApiKeyDto Dto)> CreateAsync(long userId, CreateApiKeyRequest request, CancellationToken ct = default)
    {
        var expiresAt = request.ExpiresAt is { } requested ? AsUtc(requested) : (DateTime?)null;
        ValidateScope(request.Scope, expiresAt, DateTime.UtcNow);

        var rawBytes = RandomNumberGenerator.GetBytes(32);
        // Draw the fixed-length prefix directly: filtering base64 could leave fewer than six characters.
        var prefix = RandomNumberGenerator.GetString(PrefixAlphabet, PrefixLength);
        var rawKey = $"{prefix}_{Convert.ToBase64String(rawBytes)}";
        var keyHash = HashKey(rawKey);

        var entity = new ApiKey
        {
            ApiUserId = userId,
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = prefix,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            Scope = request.Scope,
        };
        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        return (rawKey, MapDto(entity));
    }

    /// <summary>Returns a live key's owner and scope. Authentication schemes check the ban and allowed capability.</summary>
    public async Task<ApiKeyValidation?> ValidateAsync(string rawKey, CancellationToken ct = default)
    {
        var keyHash = HashKey(rawKey);
        var apiKey = await _db.ApiKeys
            .AsNoTracking()
            .Include(k => k.ApiUser)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && !k.IsRevoked, ct);

        if (apiKey is null) return null;
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt <= DateTime.UtcNow) return null;

        // Usage telemetry must not fail authentication when simultaneous requests contend on
        // api.db before reaching their endpoint's write gate. A single conditional UPDATE also
        // avoids tracking unrelated request state and never moves LastUsedAt backwards.
        var usedAt = DateTime.UtcNow;
        try
        {
            await _db.ApiKeys.Where(k => k.Id == apiKey.Id && (k.LastUsedAt == null || k.LastUsedAt < usedAt))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, usedAt), ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // SQLITE_BUSY / SQLITE_LOCKED: the credential was checked; this timestamp is best effort.
        }
        return new ApiKeyValidation(apiKey.ApiUser, apiKey.Scope);
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(long userId, CancellationToken ct = default)
    {
        var keys = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.ApiUserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return keys.Select(MapDto).ToList();
    }

    public async Task<ApiKeyDto?> GetAsync(long id, long userId, CancellationToken ct = default)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.ApiUserId == userId, ct);
        return key is null ? null : MapDto(key);
    }

    public async Task<bool> RevokeAsync(long id, long userId, CancellationToken ct = default)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.ApiUserId == userId, ct);
        if (key is null) return false;
        key.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Requires a bounded expiry for an ingest credential while retaining full keys' optional expiry.</summary>
    private static void ValidateScope(string? scope, DateTime? expiresAt, DateTime now)
    {
        if (!ApiKeyScopes.IsValid(scope))
            throw new ValidationException("Unknown API key scope.");
        if (scope != ApiKeyScopes.DiscordIngest) return;

        if (expiresAt is null)
            throw new ValidationException("A discord:ingest key needs an expiry date.");
        if (expiresAt <= now)
            throw new ValidationException("The expiry date must be in the future.");
        if (expiresAt > now.AddDays(ApiKeyScopes.MaxDiscordIngestLifetimeDays))
            throw new ValidationException(
                $"A discord:ingest key expires within {ApiKeyScopes.MaxDiscordIngestLifetimeDays} days.");
    }

    /// <summary>JSON dates with offsets arrive as local values; dates without a zone are treated as UTC.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private static string HashKey(string rawKey)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ApiKeyDto MapDto(ApiKey k) => new()
    {
        Id = k.Id,
        Name = k.Name,
        KeyPrefix = k.KeyPrefix,
        CreatedAt = k.CreatedAt,
        LastUsedAt = k.LastUsedAt,
        ExpiresAt = k.ExpiresAt,
        IsRevoked = k.IsRevoked,
        Scope = k.Scope,
    };
}
