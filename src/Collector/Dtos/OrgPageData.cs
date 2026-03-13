namespace Collector.Dtos;

/// <summary>
/// Extended organization data parsed from the full RSI org page.
/// Complements OrganizationData with text content not available in the API list.
/// </summary>
public class OrgPageData
{
    public string Sid { get; set; } = null!;
    public string? Description { get; set; }
    public string? History { get; set; }
    public string? Manifesto { get; set; }
    public string? Charter { get; set; }
    public string? FocusPrimaryName { get; set; }
    public string? FocusPrimaryImage { get; set; }
    public string? FocusSecondaryName { get; set; }
    public string? FocusSecondaryImage { get; set; }
}
