using Collector.Api.Data;
using Collector.Api.Dtos.Auth;
using Collector.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

public class AuthService
{
    private readonly ApiDbContext _db;
    private readonly TokenService _tokenService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(ApiDbContext db, TokenService tokenService, IConfiguration configuration, ILogger<AuthService> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ApiUser> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await _db.ApiUsers.AnyAsync(u => u.Username == request.Username, ct))
            throw new InvalidOperationException("Username already taken");
        if (await _db.ApiUsers.AnyAsync(u => u.Email == request.Email, ct))
            throw new InvalidOperationException("Email already registered");

        var user = new ApiUser
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.ApiUsers.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await _db.ApiUsers.FirstOrDefaultAsync(u => u.Username == request.Username, ct)
            ?? throw new UnauthorizedAccessException("Invalid username or password");

        if (user.IsBanned)
            throw new UnauthorizedAccessException("Account is banned");

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid username or password");

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return await CreateAuthResponseAsync(user, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(refreshToken);
        var stored = await _db.RefreshTokens
            .Include(t => t.ApiUser)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw new UnauthorizedAccessException("Invalid refresh token");

        if (stored.IsRevoked || stored.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Refresh token expired or revoked");

        if (stored.ApiUser.IsBanned)
            throw new UnauthorizedAccessException("Account is banned");

        // Rotate: revoke old token
        stored.IsRevoked = true;

        var response = await CreateAuthResponseAsync(stored.ApiUser, ct);
        stored.ReplacedByTokenHash = _tokenService.HashToken(response.RefreshToken);
        await _db.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (stored is not null)
        {
            stored.IsRevoked = true;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<ApiUser?> GetMeAsync(long userId, CancellationToken ct = default) =>
        await _db.ApiUsers.FindAsync([userId], ct);

    public async Task ForgotPasswordAsync(string email, CancellationToken ct = default)
    {
        var user = await _db.ApiUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null) return; // Don't leak user existence

        user.PasswordResetToken = _tokenService.GenerateResetToken();
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Password reset token generated for user {Username}: {Token}",
            user.Username, user.PasswordResetToken);
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.ApiUsers.FirstOrDefaultAsync(
            u => u.PasswordResetToken == token && u.PasswordResetTokenExpiry > DateTime.UtcNow, ct)
            ?? throw new ArgumentException("Invalid or expired reset token");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.UpdatedAt = DateTime.UtcNow;

        // Revoke all refresh tokens
        var tokens = await _db.RefreshTokens.Where(t => t.ApiUserId == user.Id && !t.IsRevoked).ToListAsync(ct);
        foreach (var t in tokens) t.IsRevoked = true;

        await _db.SaveChangesAsync(ct);
    }

    private async Task<AuthResponse> CreateAuthResponseAsync(ApiUser user, CancellationToken ct)
    {
        var (accessToken, expiresAt) = _tokenService.GenerateAccessToken(user);
        var rawRefresh = _tokenService.GenerateRefreshToken();
        var days = _configuration.GetValue("Api:RefreshTokenDays", 30);

        _db.RefreshTokens.Add(new RefreshToken
        {
            ApiUserId = user.Id,
            TokenHash = _tokenService.HashToken(rawRefresh),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(days),
        });
        await _db.SaveChangesAsync(ct);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = rawRefresh,
            ExpiresAt = expiresAt,
            User = MapUser(user),
        };
    }

    public static UserDto MapUser(ApiUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Email = user.Email,
        IsAdmin = user.IsAdmin,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt,
    };
}
