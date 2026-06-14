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
    /// <summary>Latest known display name of the organization (resolved via Organizations).</summary>
    public string? OrgName { get; set; }
    public string UserHandle { get; set; } = null!;
    public int? CitizenId { get; set; }
    public string? DisplayName { get; set; }
    public string? Rank { get; set; }
    /// <summary>Cleaned list of roles parsed from the legacy RolesJson column.</summary>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
    public string? UrlImage { get; set; }
    /// <summary>Timestamp of the latest snapshot — i.e. when the member was last seen in this org.</summary>
    public DateTime Timestamp { get; set; }
    /// <summary>
    /// Earliest snapshot in which this member was seen in this org ("member since").
    /// First observation by the tracker, which may post-date the real join date.
    /// Null when not computed (e.g. the org-members listing doesn't populate it).
    /// </summary>
    public DateTime? MemberSince { get; set; }
    public bool IsActive { get; set; }
}

public class GrowthDataPoint
{
    public string Date { get; set; } = null!;
    public int MembersCount { get; set; }
    public int Delta { get; set; }
}
