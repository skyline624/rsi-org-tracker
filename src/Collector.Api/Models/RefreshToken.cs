namespace Collector.Api.Models;

public class RefreshToken
{
    public long Id { get; set; }
    public long ApiUserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public ApiUser ApiUser { get; set; } = null!;
}
