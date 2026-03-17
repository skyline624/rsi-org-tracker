using System.Security.Cryptography;
using Collector.Api.Data;
using Collector.Api.Dtos.ApiKeys;
using Collector.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

public class ApiKeyService
{
    private readonly ApiDbContext _db;

    public ApiKeyService(ApiDbContext db)
    {
        _db = db;
    }

    public async Task<(string RawKey, ApiKeyDto Dto)> CreateAsync(long userId, CreateApiKeyRequest request, CancellationToken ct = default)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var prefix = Convert.ToBase64String(rawBytes[..6]).Replace("+", "").Replace("/", "")[..6];
        var rawKey = $"{prefix}_{Convert.ToBase64String(rawBytes)}";
        var keyHash = HashKey(rawKey);

        var entity = new ApiKey
        {
            ApiUserId = userId,
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = prefix,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt,
        };
        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        return (rawKey, MapDto(entity));
    }

    public async Task<ApiUser?> ValidateAsync(string rawKey, CancellationToken ct = default)
    {
        var keyHash = HashKey(rawKey);
        var apiKey = await _db.ApiKeys
            .Include(k => k.ApiUser)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && !k.IsRevoked, ct);

        if (apiKey is null) return null;
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt < DateTime.UtcNow) return null;

        apiKey.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return apiKey.ApiUser;
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(long userId, CancellationToken ct = default)
    {
        var keys = await _db.ApiKeys
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
    };
}
