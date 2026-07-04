using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Links;

public record CreateLinkRequest(
    [Required, MaxLength(30)] string Provider,
    [Required, MaxLength(200)] string Value);

public class LinkDto
{
    public long Id { get; set; }
    public string Provider { get; set; } = null!;
    public string Value { get; set; } = null!;
    public string AuthorUsername { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
