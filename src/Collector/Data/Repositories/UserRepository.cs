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

    public async Task<bool> ExistsAsync(int citizenId, CancellationToken ct = default)
    {
        return await DbSet.AnyAsync(u => u.CitizenId == citizenId, ct);
    }

    public async Task<Dictionary<string, string?>> GetDisplayNamesByHandlesAsync(IReadOnlyList<string> handles, CancellationToken ct = default)
    {
        return await DbSet
            .Where(u => handles.Contains(u.UserHandle))
            .ToDictionaryAsync(u => u.UserHandle, u => u.DisplayName, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<IReadOnlyList<string>> GetExistingHandlesAsync(IReadOnlyList<string> handles, CancellationToken ct = default)
    {
        if (handles.Count == 0) return Array.Empty<string>();
        return await DbSet
            .Where(u => handles.Contains(u.UserHandle))
            .Select(u => u.UserHandle)
            .ToListAsync(ct);
    }
}