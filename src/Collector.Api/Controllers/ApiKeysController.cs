using Collector.Api.Auth;
using Collector.Api.Dtos.ApiKeys;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/api-keys")]
[Authorize]
public class ApiKeysController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly CurrentUserAccessor _currentUser;

    public ApiKeysController(ApiKeyService apiKeyService, CurrentUserAccessor currentUser)
    {
        _apiKeyService = apiKeyService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiKeyDto>>> List(CancellationToken ct)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException();
        return Ok(await _apiKeyService.ListAsync(userId, ct));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiKeyDto>> Get(long id, CancellationToken ct)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException();
        var key = await _apiKeyService.GetAsync(id, userId, ct)
            ?? throw new KeyNotFoundException("API key not found");
        return Ok(key);
    }

    [HttpPost]
    public async Task<ActionResult<CreatedApiKeyDto>> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException();
        var (rawKey, dto) = await _apiKeyService.CreateAsync(userId, request, ct);

        var result = new CreatedApiKeyDto
        {
            Id = dto.Id,
            Name = dto.Name,
            KeyPrefix = dto.KeyPrefix,
            CreatedAt = dto.CreatedAt,
            ExpiresAt = dto.ExpiresAt,
            IsRevoked = dto.IsRevoked,
            RawKey = rawKey,
        };
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, result);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException();
        var ok = await _apiKeyService.RevokeAsync(id, userId, ct);
        if (!ok) throw new KeyNotFoundException("API key not found");
        return NoContent();
    }
}
