using Collector.Api.Data;
using Collector.Api.Dtos.Auth;
using Collector.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

public class AuthService
{
    /// <summary>
    /// A rotated refresh token presented again within this window is treated as a race
    /// between two tabs refreshing at once, not as theft.
    /// </summary>
    public static readonly TimeSpan RotationGracePeriod = TimeSpan.FromSeconds(30);

    private const string RevokedByRotation = "rotated";

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
        var now = DateTime.UtcNow;
        var tokenHash = _tokenService.HashToken(refreshToken);
        var stored = await _db.RefreshTokens
            .Include(t => t.ApiUser)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw new UnauthorizedAccessException("Invalid refresh token");

        if (stored.IsRevoked)
        {
            var concurrentRefresh = stored.RevokedReason == RevokedByRotation
                && stored.RevokedAt is { } revokedAt
                && now - revokedAt <= RotationGracePeriod;
            if (!concurrentRefresh)
            {
                // A token that was already rotated away is being replayed: assume it leaked
                // and end the whole session, including the tokens issued after it.
                _logger.LogWarning(
                    "Refresh token reuse detected for user {UserId} (family {FamilyId}); revoking the session",
                    stored.ApiUserId, stored.FamilyId);
                await RevokeFamilyAsync(stored, "reuse_detected", now, ct);
                throw new UnauthorizedAccessException("Refresh token expired or revoked");
            }
        }

        if (stored.ExpiresAt < now)
            throw new UnauthorizedAccessException("Refresh token expired or revoked");

        if (stored.ApiUser.IsBanned)
            throw new UnauthorizedAccessException("Account is banned");

        // Tokens issued before families existed start one on their first rotation.
        stored.FamilyId ??= NewFamilyId();
        if (!stored.IsRevoked)
        {
            stored.IsRevoked = true;
            stored.RevokedAt = now;
            stored.RevokedReason = RevokedByRotation;
        }

        var response = await CreateAuthResponseAsync(stored.ApiUser, stored.FamilyId, ct);
        stored.ReplacedByTokenHash ??= _tokenService.HashToken(response.RefreshToken);
        await _db.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (stored is not null)
        {
            await RevokeFamilyAsync(stored, "logout", DateTime.UtcNow, ct);
        }
    }

    /// <summary>Revokes every still-valid token of <paramref name="token"/>'s session.</summary>
    private async Task RevokeFamilyAsync(RefreshToken token, string reason, DateTime now, CancellationToken ct)
    {
        var family = token.FamilyId is null
            ? new List<RefreshToken> { token }
            : await _db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId).ToListAsync(ct);
        foreach (var t in family.Where(t => !t.IsRevoked))
        {
            t.IsRevoked = true;
            t.RevokedAt = now;
            t.RevokedReason = reason;
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string NewFamilyId() => Guid.NewGuid().ToString("N");

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

        // SECURITY: never log the reset token itself. A separate delivery channel (email)
        // must carry the token out-of-band. We only log that a reset was requested.
        _logger.LogInformation("Password reset token generated for user {Username}", user.Username);
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

        await RevokeAllSessionsAsync(user.Id, "password_reset", ct);
    }

    private async Task RevokeAllSessionsAsync(long userId, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tokens = await _db.RefreshTokens.Where(t => t.ApiUserId == userId && !t.IsRevoked).ToListAsync(ct);
        foreach (var t in tokens)
        {
            t.IsRevoked = true;
            t.RevokedAt = now;
            t.RevokedReason = reason;
        }
        await _db.SaveChangesAsync(ct);
    }

    private Task<AuthResponse> CreateAuthResponseAsync(ApiUser user, CancellationToken ct) =>
        CreateAuthResponseAsync(user, NewFamilyId(), ct);

    private async Task<AuthResponse> CreateAuthResponseAsync(ApiUser user, string familyId, CancellationToken ct)
    {
        var (accessToken, expiresAt) = _tokenService.GenerateAccessToken(user);
        var rawRefresh = _tokenService.GenerateRefreshToken();
        var days = _configuration.GetValue("Api:RefreshTokenDays", 30);

        _db.RefreshTokens.Add(new RefreshToken
        {
            ApiUserId = user.Id,
            TokenHash = _tokenService.HashToken(rawRefresh),
            FamilyId = familyId,
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

    /// <summary>
    /// Changes the password after verifying the current one, ends every existing session
    /// (other devices included) and returns fresh tokens for the caller.
    /// </summary>
    public async Task<AuthResponse> ChangePasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.ApiUsers.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found");

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await RevokeAllSessionsAsync(user.Id, "password_changed", ct);
        return await CreateAuthResponseAsync(user, ct);
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
