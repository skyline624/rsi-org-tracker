namespace Collector.Api.Dtos.Organizations;

public class OrganizationDto
{
    public string Sid { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? UrlImage { get; set; }
    public string? UrlCorpo { get; set; }
    public string? Archetype { get; set; }
    public string? Lang { get; set; }
    public string? Commitment { get; set; }
    public bool? Recruiting { get; set; }
    public bool? Roleplay { get; set; }
    public int MembersCount { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Description { get; set; }
    public string? FocusPrimaryName { get; set; }
    public string? FocusSecondaryName { get; set; }
}

public class OrganizationMemberDto
{
    public string OrgSid { get; set; } = null!;
    public string UserHandle { get; set; } = null!;
    public int? CitizenId { get; set; }
    public string? DisplayName { get; set; }
    public string? Rank { get; set; }
    public string? UrlImage { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsActive { get; set; }
}

public class GrowthDataPoint
{
    public string Date { get; set; } = null!;
    public int MembersCount { get; set; }
    public int Delta { get; set; }
}
