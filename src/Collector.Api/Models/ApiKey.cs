namespace Collector.Api.Models;

/// <summary>A hashed API credential, optionally restricted to a single capability.</summary>
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

    /// <summary>Null grants the owner's usual access; a scope restricts the key to its dedicated authentication scheme.</summary>
    public string? Scope { get; set; }

    public ApiUser ApiUser { get; set; } = null!;
}
