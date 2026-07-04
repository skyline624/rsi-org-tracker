using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Memberships;

/// <summary>Attach the person to an organization by SID. Via defaults to "discord".</summary>
public record CreateMembershipRequest(
    [Required, MaxLength(50)] string OrgSid,
    [MaxLength(200)] string? Rank,
    string? Via,
    DateTime? SinceDate);

public class MembershipDto
{
    public long Id { get; set; }
    public string OrgSid { get; set; } = null!;
    public string? OrgName { get; set; }
    public string? Rank { get; set; }
    public string Via { get; set; } = null!;
    public DateTime SinceDate { get; set; }
    public string AuthorUsername { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
