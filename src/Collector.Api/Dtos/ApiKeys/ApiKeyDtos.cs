using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.ApiKeys;

public record CreateApiKeyRequest(
    [Required, MinLength(1), MaxLength(100)] string Name,
    DateTime? ExpiresAt
);

public class ApiKeyDto
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
}

public class CreatedApiKeyDto : ApiKeyDto
{
    public string RawKey { get; set; } = null!;
}
