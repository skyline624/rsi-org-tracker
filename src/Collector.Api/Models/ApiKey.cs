namespace Collector.Api.Models;

public class ApiKey
{
    public long Id { get; set; }
    public long ApiUserId { get; set; }
    public string Name { get; set; } = null!;
    public string KeyHash { get; set; } = null!;
    public string KeyPrefix { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }

    public ApiUser ApiUser { get; set; } = null!;
}
