namespace Collector.Dtos;

/// <summary>
/// Parsed user profile data from RSI profile page.
/// </summary>
public class UserProfileData
{
    public int CitizenId { get; set; }
    public string Handle { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? UrlImage { get; set; }
    public string? Bio { get; set; }
    public string? Location { get; set; }
    public DateTime? Enlisted { get; set; }
}