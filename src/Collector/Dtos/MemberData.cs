namespace Collector.Dtos;

/// <summary>
/// Parsed member data from RSI API.
/// </summary>
public class MemberData
{
    public string OrgSid { get; set; } = null!;
    public string Handle { get; set; } = null!;
    public int? CitizenId { get; set; }
    public string? DisplayName { get; set; }
    public string? Rank { get; set; }
    public string[]? Roles { get; set; }
    public string? UrlImage { get; set; }
}