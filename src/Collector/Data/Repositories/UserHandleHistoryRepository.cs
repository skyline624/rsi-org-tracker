using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class UserHandleHistoryRepository : Repository<UserHandleHistory>, IUserHandleHistoryRepository
{
    public UserHandleHistoryRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<UserHandleHistory>> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(h => h.CitizenId == citizenId)
            .OrderByDescending(h => h.FirstSeen)
            .ToListAsync(ct);
    }

    public async Task<UserHandleHistory?> GetLatestAsync(int citizenId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(h => h.CitizenId == citizenId)
            .OrderByDescending(h => h.LastSeen)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<UserHandleHistory?> GetByHandleAsync(string handle, CancellationToken ct = default)
    {
        return await DbSet
            .Where(h => h.UserHandle == handle)
            .OrderByDescending(h => h.LastSeen)
            .FirstOrDefaultAsync(ct);
    }
}