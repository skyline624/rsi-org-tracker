namespace Collector.Api.Dtos.Users;

public class UserProfileDto
{
    public int CitizenId { get; set; }
    public string UserHandle { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? UrlImage { get; set; }
    public string? Bio { get; set; }
    public string? Location { get; set; }
    public DateTime? Enlisted { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UserHandleHistoryDto
{
    public string UserHandle { get; set; } = null!;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

public class UserResolveDto
{
    public int CitizenId { get; set; }
    public string CurrentHandle { get; set; } = null!;
    public string? RequestedHandle { get; set; }
    public bool HandleChanged { get; set; }
}
