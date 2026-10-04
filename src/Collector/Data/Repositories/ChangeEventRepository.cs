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

    /// <summary>
    /// One type of membership event of an org since a date, newest first, through the
    /// (OrgSid, Timestamp) index; the type is filtered on the rows it returns.
    /// </summary>
    public static IQueryable<ChangeEvent> MovementsQuery(
        IQueryable<ChangeEvent> source, string orgSid, DateTime since, string changeType)
        => source
            // "+ ''" keeps SQLite off IX_change_events_ChangeType_Timestamp, which it prefers
            // otherwise: that index would read every event of the type for all orgs.
            .Where(c => c.OrgSid == orgSid && c.Timestamp >= since && c.ChangeType + "" == changeType)
            .OrderByDescending(c => c.Timestamp);

    // The UserHandle index narrows this to the handle's own events.
    public Task<int> DeleteDeparturesSinceAsync(string orgSid, string userHandle, DateTime since, CancellationToken ct = default)
        => DbSet
            .Where(c => c.UserHandle == userHandle && c.ChangeType == "member_left" && c.OrgSid == orgSid && c.Timestamp >= since)
            .ExecuteDeleteAsync(ct);
}