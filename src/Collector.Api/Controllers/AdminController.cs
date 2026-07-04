using Collector.Api.Data;
using Collector.Api.Dtos.Admin;
using Collector.Api.Dtos.Auth;
using Collector.Api.Dtos.Common;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly ApiDbContext _db;
    private readonly ActivityLogService _activityLog;
    private readonly AuthService _authService;

    public AdminController(ApiDbContext db, ActivityLogService activityLog, AuthService authService)
    {
        _db = db;
        _activityLog = activityLog;
        _authService = authService;
    }

    [HttpGet("users")]
    public async Task<ActionResult<PaginatedResponse<AdminUserDto>>> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var total = await _db.ApiUsers.CountAsync(ct);
        var users = await _db.ApiUsers
            .Include(u => u.ApiKeys)
            .OrderBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PaginatedResponse<AdminUserDto>
        {
            Items = users.Select(u => new AdminUserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                IsAdmin = u.IsAdmin,
                IsBanned = u.IsBanned,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
                ApiKeyCount = u.ApiKeys.Count(k => !k.IsRevoked),
            }).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("users/{id:long}")]
    public async Task<ActionResult<AdminUserDto>> GetUser(long id, CancellationToken ct)
    {
        var user = await _db.ApiUsers
            .Include(u => u.ApiKeys)
            .FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new KeyNotFoundException($"User {id} not found");

        return Ok(new AdminUserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            IsAdmin = user.IsAdmin,
            IsBanned = user.IsBanned,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            ApiKeyCount = user.ApiKeys.Count(k => !k.IsRevoked),
        });
    }

    [HttpPut("users/{id:long}")]
    public async Task<ActionResult<AdminUserDto>> UpdateUser(long id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _db.ApiUsers.Include(u => u.ApiKeys).FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new KeyNotFoundException($"User {id} not found");

        if (request.IsAdmin.HasValue) user.IsAdmin = request.IsAdmin.Value;
        if (request.IsBanned.HasValue) user.IsBanned = request.IsBanned.Value;
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new AdminUserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            IsAdmin = user.IsAdmin,
            IsBanned = user.IsBanned,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            ApiKeyCount = user.ApiKeys.Count(k => !k.IsRevoked),
        });
    }

    [HttpDelete("users/{id:long}")]
    public async Task<IActionResult> DeleteUser(long id, CancellationToken ct)
    {
        var user = await _db.ApiUsers.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"User {id} not found");

        // activity_logs has a NO ACTION FK to api_users; detach them (keep the audit
        // trail) so deleting a user who has logged in doesn't violate the constraint.
        await _db.ActivityLogs
            .Where(l => l.ApiUserId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ApiUserId, (long?)null), ct);

        _db.ApiUsers.Remove(user);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("logs")]
    public async Task<ActionResult<PaginatedResponse<ActivityLogDto>>> GetLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] long? userId = null,
        CancellationToken ct = default)
    {
        var (items, total) = await _activityLog.GetLogsAsync(page, pageSize, userId, ct);
        return Ok(new PaginatedResponse<ActivityLogDto>
        {
            Items = items.Select(l => new ActivityLogDto
            {
                Id = l.Id,
                ApiUserId = l.ApiUserId,
                Username = l.ApiUser?.Username,
                Action = l.Action,
                EntityType = l.EntityType,
                EntityId = l.EntityId,
                IpAddress = l.IpAddress,
                CreatedAt = l.CreatedAt,
            }).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsDto>> GetStats(CancellationToken ct)
    {
        var totalUsers = await _db.ApiUsers.CountAsync(ct);
        var bannedUsers = await _db.ApiUsers.CountAsync(u => u.IsBanned, ct);
        var totalApiKeys = await _db.ApiKeys.CountAsync(ct);
        var activeApiKeys = await _db.ApiKeys.CountAsync(k => !k.IsRevoked, ct);
        var totalLogs = await _db.ActivityLogs.LongCountAsync(ct);

        return Ok(new AdminStatsDto
        {
            TotalUsers = totalUsers,
            ActiveUsers = totalUsers - bannedUsers,
            BannedUsers = bannedUsers,
            TotalApiKeys = totalApiKeys,
            ActiveApiKeys = activeApiKeys,
            TotalActivityLogs = totalLogs,
        });
    }

    // Create a new account (admin only). Unlike public register, there is no auto-login.
    [HttpPost("users")]
    public async Task<ActionResult<AdminUserDto>> CreateUser([FromBody] CreateAccountRequest request, CancellationToken ct)
    {
        var user = await _authService.RegisterAsync(
            new RegisterRequest(request.Username, request.Email, request.Password), ct);

        if (request.IsAdmin)
        {
            user.IsAdmin = true;
            await _db.SaveChangesAsync(ct);
        }

        await _activityLog.LogAsync("admin_create_user", user.Id, "user", user.Id.ToString(), null, ct);

        return Ok(new AdminUserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            IsAdmin = user.IsAdmin,
            IsBanned = user.IsBanned,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            ApiKeyCount = 0,
        });
    }
}
