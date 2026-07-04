using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Notes;

public record CreateOrgNoteRequest([Required, MaxLength(10000)] string Body);

public record UpdateOrgNoteRequest([Required, MaxLength(10000)] string Body);

public class OrgNoteDto
{
    public long Id { get; set; }
    public string OrgSid { get; set; } = null!;
    public long AuthorApiUserId { get; set; }
    public string AuthorUsername { get; set; } = null!;
    public string Body { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
