using System.Text.RegularExpressions;
using Collector.Api.Dtos.Admin;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Endpoints for manually curating tracker data (tracker.db): people the collector
/// can't reach (redacted / roster-only) and organizations it hasn't discovered
/// (private / brand new). Open to any authenticated user.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminDataController : ControllerBase
{
    private readonly ITrackedEntityRepository _entities;
    private readonly IOrganizationRepository _orgs;

    public AdminDataController(ITrackedEntityRepository entities, IOrganizationRepository orgs)
    {
        _entities = entities;
        _orgs = orgs;
    }

    /// <summary>Manually create a tracked person (e.g. a redacted RSI account).</summary>
    [HttpPost("entities")]
    public async Task<ActionResult<TrackedEntityDto>> CreateEntity(
        [FromBody] CreateEntityRequest request, CancellationToken ct)
    {
        var handle = string.IsNullOrWhiteSpace(request.Handle) ? null : request.Handle.Trim();
        if (handle is null && request.CitizenId is null)
            return BadRequest(new { message = "Provide at least a handle or a citizen id." });

        // Don't fork a duplicate manual entity for a handle we already track.
        if (handle is not null)
        {
            var existing = await _entities.GetByHandleAsync(handle, ct);
            if (existing is not null)
                return Conflict(new { message = $"An entity already exists for handle '{handle}'." });
        }

        var now = DateTime.UtcNow;
        var entity = new TrackedEntity
        {
            CitizenId = request.CitizenId,
            CurrentHandle = handle,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName!.Trim(),
            Source = TrackedEntitySource.Manual,
            Status = TrackedEntityStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _entities.AddAsync(entity, ct);
        await _entities.SaveChangesAsync(ct);

        return Ok(ToDto(entity));
    }

    /// <summary>Manually create an organization (private or not yet discovered).</summary>
    [HttpPost("organizations")]
    public async Task<ActionResult<OrganizationSummaryDto>> CreateOrganization(
        [FromBody] CreateOrganizationRequest request, CancellationToken ct)
    {
        // RSI SIDs are upper-case, at most 10 characters of A-Z, 0-9, '_' and '-'. Storing
        // them normalised keeps lookups (which upper-case the SID) working.
        var sid = request.Sid.Trim().ToUpperInvariant();
        if (!SidPattern.IsMatch(sid))
            return BadRequest(new { message = "SID must be 1-10 characters: A-Z, 0-9, '_' or '-'." });
        if (!IsSafeImageUrl(request.UrlImage))
            return BadRequest(new { message = "Image URL must be an http(s) URL or a site-relative path." });

        // Only for organizations the tracker doesn't know: a manual snapshot of a known SID
        // would become its latest snapshot and hide the collected data.
        if (await _orgs.GetLatestBySidAsync(sid, ct) is not null)
            return Conflict(new { message = $"Organization '{sid}' already exists." });

        // A manual org is just a new snapshot in `organizations`. The collector only ever
        // appends further snapshots (keyed by Sid + Timestamp), so it never overwrites or
        // deletes this one; and since it's not enqueued in discovered_organizations, it
        // can never be tombstoned either.
        var org = new Organization
        {
            Sid = sid,
            Timestamp = DateTime.UtcNow,
            Name = request.Name.Trim(),
            UrlImage = request.UrlImage,
            Archetype = request.Archetype,
            Description = request.Description,
            MembersCount = 0,
            ContentCollected = false,
            Source = OrganizationSource.Manual,
        };
        await _orgs.AddAsync(org, ct);
        await _orgs.SaveChangesAsync(ct);

        return Ok(new OrganizationSummaryDto
        {
            Sid = org.Sid,
            Name = org.Name,
            Source = org.Source,
            Timestamp = org.Timestamp,
        });
    }

    private static readonly Regex SidPattern = new("^[A-Z0-9_-]{1,10}$", RegexOptions.Compiled);

    private static bool IsSafeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (url.StartsWith('/') && !url.StartsWith("//")) return true; // RSI-relative (/media/…)
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    private static TrackedEntityDto ToDto(TrackedEntity e) => new()
    {
        Id = e.Id,
        CitizenId = e.CitizenId,
        CurrentHandle = e.CurrentHandle,
        DisplayName = e.DisplayName,
        Source = e.Source,
        Status = e.Status,
        CreatedAt = e.CreatedAt,
    };
}
