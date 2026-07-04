namespace Collector.Api.Dtos.Discord;

public class DiscordUserDto
{
    public string Id { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Badges { get; set; } = new();
}
