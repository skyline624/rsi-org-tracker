namespace Collector.Api.Models;

public class ActivityLog
{
    public long Id { get; set; }
    public long? ApiUserId { get; set; }
    public string Action { get; set; } = null!;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }

    public ApiUser? ApiUser { get; set; }
}
