namespace Collector.Api.Dtos.Changes;

public class ChangeEventDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string EntityType { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public string ChangeType { get; set; } = null!;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? OrgSid { get; set; }
    public string? UserHandle { get; set; }
}

public class ChangeSummaryDto
{
    public string ChangeType { get; set; } = null!;
    public int Count { get; set; }
}
