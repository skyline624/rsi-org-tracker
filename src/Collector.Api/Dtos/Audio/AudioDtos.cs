namespace Collector.Api.Dtos.Audio;

public class AudioDto
{
    public long Id { get; set; }
    public long TrackedEntityId { get; set; }
    public long AuthorApiUserId { get; set; }
    public string AuthorUsername { get; set; } = null!;
    public string OriginalName { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public double? DurationSec { get; set; }
    public DateTime CreatedAt { get; set; }
}
