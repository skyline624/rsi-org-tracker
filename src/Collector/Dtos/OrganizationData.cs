namespace Collector.Dtos;

/// <summary>
/// Parsed organization data from RSI API.
/// </summary>
public class OrganizationData
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
}