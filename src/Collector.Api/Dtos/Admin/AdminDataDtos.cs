using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Admin;

/// <summary>
/// Manually register a tracked person the collector can't reach — typically a
/// "redacted" RSI account with no public citizen number. At least a handle or a
/// citizen id must be provided.
/// </summary>
public record CreateEntityRequest(
    [MaxLength(100)] string? Handle,
    [MaxLength(500)] string? DisplayName,
    int? CitizenId);

public class TrackedEntityDto
{
    public long Id { get; set; }
    public int? CitizenId { get; set; }
    public string? CurrentHandle { get; set; }
    public string? DisplayName { get; set; }
    public string Source { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Manually register an organization the collector hasn't discovered (private or
/// brand new). Stored as a snapshot in <c>organizations</c>; the collector only
/// appends further snapshots, so it never overwrites or deletes this row.
/// </summary>
public record CreateOrganizationRequest(
    [Required, MaxLength(50)] string Sid,
    [Required, MaxLength(500)] string Name,
    [MaxLength(2000)] string? UrlImage,
    [MaxLength(100)] string? Archetype,
    string? Description);

public class OrganizationSummaryDto
{
    public string Sid { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Source { get; set; } = null!;
    public DateTime Timestamp { get; set; }
}

/// <summary>Admin-created account (no auto-login).</summary>
public record CreateAccountRequest(
    [Required, MinLength(3), MaxLength(100)] string Username,
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    bool IsAdmin = false);
