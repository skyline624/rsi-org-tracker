using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class UserRepository : Repository<User>, IUserRepository
{
    public UserRepository(TrackerDbContext context) : base(context) { }

    public async Task<User?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default)
    {
        return await DbSet.FirstOrDefaultAsync(u => u.CitizenId == citizenId, ct);
    }

    public async Task<User?> GetByHandleAsync(string handle, CancellationToken ct = default)
    {
        return await DbSet.FirstOrDefaultAsync(u => u.UserHandle == handle, ct);
    }

    public async Task<Dictionary<string, int>> GetCitizenIdsByHandlesAsync(
        IReadOnlyCollection<string> handles, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (handles.Count == 0) return result;

        var current = await DbSet.AsNoTracking()
            .Where(u => handles.Contains(u.UserHandle))
            .Select(u => new { u.UserHandle, u.CitizenId, u.UpdatedAt })
            .ToListAsync(ct);
        foreach (var row in current.OrderByDescending(r => r.UpdatedAt))
        {
            result.TryAdd(row.UserHandle, row.CitizenId);
        }

        var former = await Context.UserHandleHistories.AsNoTracking()
            .Where(h => handles.Contains(h.UserHandle))
            .Select(h => new { h.UserHandle, h.CitizenId, h.LastSeen })
            .ToListAsync(ct);
        foreach (var row in former.OrderByDescending(r => r.LastSeen))
        {
            result.TryAdd(row.UserHandle, row.CitizenId);
        }

        return result;
    }
}