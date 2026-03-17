using Collector.Api.Data;
using Collector.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

public class ActivityLogService
{
    private readonly ApiDbContext _db;

    public ActivityLogService(ApiDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(string action, long? userId = null, string? entityType = null,
        string? entityId = null, string? ipAddress = null, CancellationToken ct = default)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            ApiUserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = ipAddress,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<ActivityLog> Items, int Total)> GetLogsAsync(
        int page, int pageSize, long? userId = null, CancellationToken ct = default)
    {
        var query = _db.ActivityLogs
            .Include(l => l.ApiUser)
            .AsQueryable();

        if (userId.HasValue)
            query = query.Where(l => l.ApiUserId == userId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }
}
