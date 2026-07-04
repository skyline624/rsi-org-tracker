namespace Collector.Api.Dtos.Memberships;

/// <summary>A person manually attached to an organization (not from the collected roster).</summary>
public class OrgMemberDto
{
    public string Handle { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? Rank { get; set; }
    public string Via { get; set; } = null!;
    public DateTime SinceDate { get; set; }
}
