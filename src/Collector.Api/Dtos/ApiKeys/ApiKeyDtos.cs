using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.ApiKeys;

/// <summary>Creates a full key, or an ingest-only key that must expire within a year.</summary>
public record CreateApiKeyRequest(
    [Required, MinLength(1), MaxLength(100)] string Name,
    DateTime? ExpiresAt,
    string? Scope = null
);

/// <summary>Public metadata for a key; the raw credential is never included.</summary>
public class ApiKeyDto
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public string? Scope { get; set; }
}

/// <summary>Includes the raw key once, immediately after creation.</summary>
public class CreatedApiKeyDto : ApiKeyDto
{
    public string RawKey { get; set; } = null!;

    public static CreatedApiKeyDto From(ApiKeyDto dto, string rawKey) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        KeyPrefix = dto.KeyPrefix,
        CreatedAt = dto.CreatedAt,
        LastUsedAt = dto.LastUsedAt,
        ExpiresAt = dto.ExpiresAt,
        IsRevoked = dto.IsRevoked,
        Scope = dto.Scope,
        RawKey = rawKey,
    };
}
