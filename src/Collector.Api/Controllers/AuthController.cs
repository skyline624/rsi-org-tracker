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

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var user = await _authService.RegisterAsync(request, ct);
        await _activityLog.LogAsync("register", user.Id, "user", user.Id.ToString(), _currentUser.IpAddress, ct);

        var loginResult = await _authService.LoginAsync(new LoginRequest(request.Username, request.Password), ct);
        return Ok(loginResult);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _authService.LoginAsync(request, ct);
        await _activityLog.LogAsync("login", result.User.Id, "user", result.User.Id.ToString(), _currentUser.IpAddress, ct);
        return Ok(result);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken, ct);
        return Ok(result);
    }

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
        var userId = _currentUser.UserId ?? throw new UnauthorizedAccessException();
        var user = await _authService.GetMeAsync(userId, ct)
            ?? throw new KeyNotFoundException("User not found");
        return Ok(AuthService.MapUser(user));
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await _authService.ForgotPasswordAsync(request.Email, ct);
        return Ok(new { message = "If that email is registered, a reset token has been sent." });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await _authService.ResetPasswordAsync(request.Token, request.NewPassword, ct);
        return Ok(new { message = "Password reset successfully" });
    }
}
