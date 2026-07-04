using Collector.Api.Auth;
using Collector.Api.Dtos.Audio;
using Collector.Api.Services;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>
/// Voice recordings (uploaded audio) attached to a person via the stable
/// <see cref="TrackedEntity"/>. Keyed by handle for the UI; the entity is resolved
/// (and lazily created on first upload).
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class AudioController : ControllerBase
{
    private const long MaxBytes = 25L * 1024 * 1024; // 25 MB

    private static readonly Dictionary<string, string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".webm"] = "audio/webm",
    };

    private readonly IEntityResolver _resolver;
    private readonly ITrackedEntityRepository _entities;
    private readonly IEntityAudioRepository _audio;
    private readonly IUserRepository _users;
    private readonly AudioStorageService _storage;
    private readonly CurrentUserAccessor _currentUser;

    public AudioController(
        IEntityResolver resolver,
        ITrackedEntityRepository entities,
        IEntityAudioRepository audio,
        IUserRepository users,
        AudioStorageService storage,
        CurrentUserAccessor currentUser)
    {
        _resolver = resolver;
        _entities = entities;
        _audio = audio;
        _users = users;
        _storage = storage;
        _currentUser = currentUser;
    }

    [HttpGet("users/{handle}/audio")]
    public async Task<ActionResult<IReadOnlyList<AudioDto>>> GetAudio(string handle, CancellationToken ct)
    {
        var entity = await ResolveExistingAsync(handle, ct);
        if (entity is null) return Ok(Array.Empty<AudioDto>());
        var items = await _audio.GetByEntityIdAsync(entity.Id, ct);
        return Ok(items.Select(ToDto).ToList());
    }

    [HttpPost("users/{handle}/audio")]
    [RequestSizeLimit(MaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBytes)]
    public async Task<ActionResult<AudioDto>> UploadAudio(string handle, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });
        if (file.Length > MaxBytes)
            return BadRequest(new { message = "File exceeds 25 MB." });

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExt.TryGetValue(ext, out var expectedMime))
            return BadRequest(new { message = "Unsupported format. Allowed: mp3, ogg, m4a, webm." });

        // Declared content type must look like audio (basic sniffing).
        if (!string.IsNullOrEmpty(file.ContentType)
            && !file.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Declared content type is not audio." });

        var user = await _users.GetByHandleAsync(handle, ct);
        var entityId = await _resolver.ResolveOrCreateAsync(user?.CitizenId, handle, user?.DisplayName, ct);

        string relPath;
        long size;
        await using (var stream = file.OpenReadStream())
        {
            (relPath, size) = await _storage.SaveAsync(entityId, stream, ext, ct);
        }

        var row = new EntityAudio
        {
            TrackedEntityId = entityId,
            AuthorApiUserId = _currentUser.UserId ?? 0,
            AuthorUsername = _currentUser.Username ?? "unknown",
            OriginalName = Path.GetFileName(file.FileName),
            StoredPath = relPath,
            MimeType = expectedMime,
            SizeBytes = size,
            DurationSec = null,
            CreatedAt = DateTime.UtcNow,
        };
        await _audio.AddAsync(row, ct);
        await _audio.SaveChangesAsync(ct);
        return Ok(ToDto(row));
    }

    [HttpGet("audio/{id:long}")]
    public async Task<IActionResult> StreamAudio(long id, CancellationToken ct)
    {
        var row = await _audio.GetByIdAsync(id, ct);
        if (row is null) return NotFound();

        var full = _storage.GetFullPath(row.StoredPath);
        if (!System.IO.File.Exists(full)) return NotFound();

        // enableRangeProcessing lets the browser seek within the audio.
        return PhysicalFile(full, row.MimeType, enableRangeProcessing: true);
    }

    [HttpDelete("audio/{id:long}")]
    public async Task<IActionResult> DeleteAudio(long id, CancellationToken ct)
    {
        var row = await _audio.GetByIdAsync(id, ct);
        if (row is null) return NotFound();
        if (!(_currentUser.IsAdmin || row.AuthorApiUserId == (_currentUser.UserId ?? -1)))
            return Forbid();

        _storage.Delete(row.StoredPath);
        _audio.Remove(row);
        await _audio.SaveChangesAsync(ct);
        return NoContent();
    }

    // Resolve an EXISTING entity for a handle without creating one (for reads).
    private async Task<TrackedEntity?> ResolveExistingAsync(string handle, CancellationToken ct)
    {
        var user = await _users.GetByHandleAsync(handle, ct);
        if (user is not null)
        {
            var byCid = await _entities.GetByCitizenIdAsync(user.CitizenId, ct);
            if (byCid is not null) return byCid;
        }
        return await _entities.GetByHandleAsync(handle, ct);
    }

    private static AudioDto ToDto(EntityAudio a) => new()
    {
        Id = a.Id,
        TrackedEntityId = a.TrackedEntityId,
        AuthorApiUserId = a.AuthorApiUserId,
        AuthorUsername = a.AuthorUsername,
        OriginalName = a.OriginalName,
        MimeType = a.MimeType,
        SizeBytes = a.SizeBytes,
        DurationSec = a.DurationSec,
        CreatedAt = a.CreatedAt,
    };
}
