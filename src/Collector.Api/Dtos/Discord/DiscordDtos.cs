namespace Collector.Api.Dtos.Discord;

public class DiscordUserDto
{
    public string Id { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Badges { get; set; } = new();
}

/// <summary>
/// What to enter in the Vencord plugin: the tracker's public URL and the SHA-256
/// fingerprint of its certificate. Each is null while the administrator has not set it.
/// </summary>
public class DiscordIngestConfigDto
{
    public string? PublicUrl { get; set; }
    public string? CertificateSha256 { get; set; }
}
