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

        var unchanged = latest != null && latest.TotalRows == counts.TotalRows
            && (counts.VisibleCount == null
                || (latest.VisibleCount == counts.VisibleCount
                    && latest.RedactedCount == counts.RedactedCount
                    && latest.HiddenCount == counts.HiddenCount));
        if (unchanged) return (false, latest);

        DbSet.Add(counts);
        await Context.SaveChangesAsync(ct);
        Context.Entry(counts).State = EntityState.Detached;
        return (true, latest);
    }
}
