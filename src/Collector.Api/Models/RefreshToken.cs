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

    /// <summary>
    /// Session identifier shared by every token issued by rotation from the same login.
    /// Null for tokens issued before families existed (they get one on first rotation).
    /// </summary>
    public string? FamilyId { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>rotated, reuse_detected, logout, password_changed, password_reset.</summary>
    public string? RevokedReason { get; set; }

    public ApiUser ApiUser { get; set; } = null!;
}
