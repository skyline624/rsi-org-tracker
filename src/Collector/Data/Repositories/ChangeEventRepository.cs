using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class ChangeEventRepository : Repository<ChangeEvent>, IChangeEventRepository
{
    public ChangeEventRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<ChangeEvent>> GetByOrgSidAsync(string orgSid, int limit = 100, CancellationToken ct = default)
    {
        return await DbSet
            .Where(c => c.OrgSid == orgSid)
            .OrderByDescending(c => c.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChangeEvent>> GetByUserHandleAsync(string userHandle, int limit = 100, CancellationToken ct = default)
    {
        return await DbSet
            .Where(c => c.UserHandle == userHandle)
            .OrderByDescending(c => c.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
    }

    // The UserHandle index narrows this to the handle's own events.
    public Task<int> DeleteDeparturesSinceAsync(string orgSid, string userHandle, DateTime since, CancellationToken ct = default)
        => DbSet
            .Where(c => c.UserHandle == userHandle && c.ChangeType == "member_left" && c.OrgSid == orgSid && c.Timestamp >= since)
            .ExecuteDeleteAsync(ct);
}