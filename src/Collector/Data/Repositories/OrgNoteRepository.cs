using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrgNoteRepository : Repository<OrgNote>, IOrgNoteRepository
{
    public OrgNoteRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<OrgNote>> GetByOrgSidAsync(string orgSid, CancellationToken ct = default)
    {
        // SIDs are stored in upper case: compare as-is so the OrgSid index is used.
        var sid = orgSid.Trim().ToUpperInvariant();
        return await DbSet.AsNoTracking()
            .Where(n => n.OrgSid == sid)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    public void Remove(OrgNote note) => DbSet.Remove(note);
}
