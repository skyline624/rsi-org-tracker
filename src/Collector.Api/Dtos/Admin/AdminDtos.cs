using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Admin;

public class AdminUserDto
{
    public long Id { get; set; }
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public bool IsAdmin { get; set; }
    public bool IsBanned { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int ApiKeyCount { get; set; }
}

public record UpdateUserRequest(
    bool? IsAdmin,
    bool? IsBanned
);

public class ActivityLogDto
{
    public long Id { get; set; }
    public long? ApiUserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = null!;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminStatsDto
{
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int BannedUsers { get; set; }
    public int TotalApiKeys { get; set; }
    public int ActiveApiKeys { get; set; }
    public long TotalActivityLogs { get; set; }
}
