using Collector.Api.Auth;
using Collector.Api.Errors;
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

    /// <summary>Consecutive failures that lock an account; each further batch doubles the lock.</summary>
    public const int MaxFailedLogins = 5;
    private static readonly TimeSpan BaseLockout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromHours(24);
    private const string InvalidCredentials = "Invalid username or password";

    /// <summary>
    /// Hash checked when the username does not exist, so an unknown account costs the same
    /// BCrypt work as a wrong password and response times do not reveal which accounts exist.
    /// </summary>
    private static readonly Lazy<string> DummyHash =
        new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()));

    private readonly ApiDbContext _db;
    private readonly TokenService _tokenService;
    private readonly IPasswordHasher _passwords;
    private readonly ActivityLogService _activityLog;
    private readonly CurrentUserAccessor _currentUser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        ApiDbContext db,
        TokenService tokenService,
        IPasswordHasher passwords,
        ActivityLogService activityLog,
        CurrentUserAccessor currentUser,
        IConfiguration configuration,
        ILogger<AuthService> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _passwords = passwords;
        _activityLog = activityLog;
        _currentUser = currentUser;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ApiUser> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await _db.ApiUsers.AnyAsync(u => u.Username == request.Username, ct))
            throw new ConflictException("Username already taken");
        if (await _db.ApiUsers.AnyAsync(u => u.Email == request.Email, ct))
            throw new ConflictException("Email already registered");

        var user = new ApiUser
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = _passwords.Hash(request.Password),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.ApiUsers.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>
    /// Every failure answers the same generic message (unknown user, wrong password, locked
    /// account): only someone who knows the password learns that an account is banned.
    /// </summary>
    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = await _db.ApiUsers.FirstOrDefaultAsync(u => u.Username == request.Username, ct);
        var passwordOk = _passwords.Verify(request.Password, user?.PasswordHash ?? DummyHash.Value);

        if (user is null)
        {
            await _activityLog.LogAsync("login_failed", null, "user", null, _currentUser.IpAddress, ct);
            throw new AuthenticationFailedException(InvalidCredentials);
        }

        if (user.LockoutEnd > now)
        {
            await _activityLog.LogAsync("login_locked", user.Id, "user", user.Id.ToString(), _currentUser.IpAddress, ct);
            throw new AuthenticationFailedException(InvalidCredentials);
        }

        if (!passwordOk)
        {
            await RecordFailedLoginAsync(user, now, ct);
            throw new AuthenticationFailedException(InvalidCredentials);
        }

        if (user.IsBanned)
            throw new AuthenticationFailedException("Account is banned");

        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);

        return await CreateAuthResponseAsync(user, ct);
    }

    private async Task RecordFailedLoginAsync(ApiUser user, DateTime now, CancellationToken ct)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount % MaxFailedLogins == 0)
        {
            var lockouts = user.FailedLoginCount / MaxFailedLogins;
            var duration = TimeSpan.FromTicks(Math.Min(
                BaseLockout.Ticks * (1L << Math.Min(lockouts - 1, 16)), MaxLockout.Ticks));
            user.LockoutEnd = now + duration;
            _logger.LogWarning("Account {UserId} locked for {Minutes} min after {Count} failed logins",
                user.Id, duration.TotalMinutes, user.FailedLoginCount);
        }
        await _db.SaveChangesAsync(ct);
        await _activityLog.LogAsync("login_failed", user.Id, "user", user.Id.ToString(), _currentUser.IpAddress, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tokenHash = _tokenService.HashToken(refreshToken);
        var stored = await _db.RefreshTokens
            .Include(t => t.ApiUser)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw new AuthenticationFailedException("Invalid refresh token");

        if (stored.IsRevoked)
        {
            // A session ended on purpose (logout, password change, reuse) stays ended: the
            // grace below is only for requests racing a rotation. No reuse alarm either.
            if (stored.RevokedReason != RevokedByRotation || await SessionEndedAsync(stored, ct))
                throw new AuthenticationFailedException("Refresh token expired or revoked");

            var concurrentRefresh = stored.RevokedAt is { } revokedAt && now - revokedAt <= RotationGracePeriod;
            if (!concurrentRefresh)
            {
                // A token that was already rotated away is being replayed: assume it leaked
                // and end the whole session, including the tokens issued after it.
                _logger.LogWarning(
                    "Refresh token reuse detected for user {UserId} (family {FamilyId}); revoking the session",
                    stored.ApiUserId, stored.FamilyId);
                await RevokeFamilyAsync(stored, "reuse_detected", now, ct);
                throw new AuthenticationFailedException("Refresh token expired or revoked");
            }
        }

        if (stored.ExpiresAt < now)
            throw new AuthenticationFailedException("Refresh token expired or revoked");

        if (stored.ApiUser.IsBanned)
            throw new AuthenticationFailedException("Account is banned");

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

    /// <summary>
    /// Whether the token's session was ended on purpose: a token of its family revoked
    /// for another reason than a rotation. Rotated tokens keep their reason when the
    /// family is revoked afterwards.
    /// </summary>
    private Task<bool> SessionEndedAsync(RefreshToken token, CancellationToken ct) =>
        token.FamilyId is null
            ? Task.FromResult(false)
            : _db.RefreshTokens.AnyAsync(t => t.FamilyId == token.FamilyId && t.IsRevoked
                && t.RevokedReason != RevokedByRotation, ct);

    private static string NewFamilyId() => Guid.NewGuid().ToString("N");

    public async Task<ApiUser?> GetMeAsync(long userId, CancellationToken ct = default) =>
        await _db.ApiUsers.FindAsync([userId], ct);

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
            ?? throw new AuthenticationFailedException("User not found");

        if (!_passwords.Verify(currentPassword, user.PasswordHash))
            throw new AuthenticationFailedException("Current password is incorrect");

        user.PasswordHash = _passwords.Hash(newPassword);
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
