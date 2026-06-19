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
        if (handles.Count == 0) return new(StringComparer.OrdinalIgnoreCase);

        // A handle can map to MORE THAN ONE users row — handle reuse between two
        // citizens (UserHandle is not unique; only CitizenId is), or case variants
        // colliding under the OrdinalIgnoreCase key. Dedupe to the most-recently
        // updated row instead of letting ToDictionary throw on the duplicate key,
        // which used to abort member collection for any org containing such a
        // handle (e.g. "Harion", "Gallus").
        var rows = await DbSet
            .Where(u => handles.Contains(u.UserHandle))
            .Select(u => new { u.UserHandle, u.DisplayName, u.UpdatedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.UserHandle, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.UpdatedAt).First().DisplayName,
                StringComparer.OrdinalIgnoreCase);
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