namespace Collector.Api.Dtos.Common;

public class ErrorResponse
{
    public string Error { get; set; } = null!;
    public string? Detail { get; set; }
}
