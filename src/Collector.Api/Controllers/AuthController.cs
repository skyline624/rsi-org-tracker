using Collector.Api.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Collector.Api.Errors;
using Collector.Api.Auth;
using Collector.Api.Dtos.Auth;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ActivityLogService _activityLog;
    private readonly CurrentUserAccessor _currentUser;

    public AuthController(AuthService authService, ActivityLogService activityLog, CurrentUserAccessor currentUser)
    {
        _authService = authService;
        _activityLog = activityLog;
        _currentUser = currentUser;
    }

    // Le site est privé : pas d'inscription ni de réinitialisation par email. Les comptes
    // sont créés par un administrateur (POST /api/admin/users).

    // Public key set used by the web front to verify access tokens (RS256).
    [AllowAnonymous]
    [HttpGet("jwks")]
    public IActionResult Jwks([FromServices] JwtKeyProvider keys) =>
        Ok(new { keys = new[] { keys.PublicJwk } });

    [EnableRateLimiting(RateLimitingExtensions.LoginPolicy)]
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _authService.LoginAsync(request, ct);
        await _activityLog.LogAsync("login", result.User.Id, "user", result.User.Id.ToString(), _currentUser.IpAddress, ct);
        return Ok(result);
    }

    [EnableRateLimiting(RateLimitingExtensions.RefreshPolicy)]
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken, ct);
        return Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        await _authService.LogoutAsync(request.RefreshToken, ct);
        return Ok(new { message = "Logged out" });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var userId = _currentUser.UserId ?? throw new AuthenticationFailedException("Authentication required");
        var user = await _authService.GetMeAsync(userId, ct)
            ?? throw new NotFoundException("User not found");
        return Ok(AuthService.MapUser(user));
    }

    // Change the current user's password (requires the current password).
    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult<AuthResponse>> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId ?? throw new AuthenticationFailedException("Authentication required");
        var result = await _authService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, ct);
        await _activityLog.LogAsync("change_password", userId, "user", userId.ToString(), _currentUser.IpAddress, ct);
        return Ok(result);
    }
}
