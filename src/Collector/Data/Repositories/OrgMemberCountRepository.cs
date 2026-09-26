using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrgMemberCountRepository : Repository<OrgMemberCount>, IOrgMemberCountRepository
{
    public OrgMemberCountRepository(TrackerDbContext context) : base(context) { }

    public async Task<(bool Written, OrgMemberCount? Previous)> RecordIfChangedAsync(
        OrgMemberCount counts, CancellationToken ct = default)
    {
        var latest = await DbSet.AsNoTracking()
            .Where(c => c.OrgSid == counts.OrgSid)
            .OrderByDescending(c => c.CollectedAt)
            .FirstOrDefaultAsync(ct);

        // A split that becomes unknown is a change too: after an incomplete read, the
        // latest row must not keep the visible count of an earlier complete one.
        var unchanged = latest != null && latest.TotalRows == counts.TotalRows
            && latest.VisibleCount == counts.VisibleCount
            && latest.RedactedCount == counts.RedactedCount
            && latest.HiddenCount == counts.HiddenCount;
        if (unchanged) return (false, latest);

        DbSet.Add(counts);
        try
        {
            await Context.SaveChangesAsync(ct);
        }
        finally
        {
            // Also after a failed save: left tracked, the roster transaction that follows
            // in MemberCollector would insert it again.
            Context.Entry(counts).State = EntityState.Detached;
        }
        return (true, latest);
    }
}
