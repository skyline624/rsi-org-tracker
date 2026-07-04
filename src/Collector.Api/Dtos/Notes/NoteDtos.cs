using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Notes;

public record CreateNoteRequest([Required, MaxLength(10000)] string Body);

public record UpdateNoteRequest([Required, MaxLength(10000)] string Body);

public class NoteDto
{
    public long Id { get; set; }
    public long TrackedEntityId { get; set; }
    public long AuthorApiUserId { get; set; }
    public string AuthorUsername { get; set; } = null!;
    public string Body { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
