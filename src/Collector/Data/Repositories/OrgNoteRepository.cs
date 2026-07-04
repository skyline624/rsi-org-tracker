using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class OrgNoteRepository : Repository<OrgNote>, IOrgNoteRepository
{
    public OrgNoteRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<OrgNote>> GetByOrgSidAsync(string orgSid, CancellationToken ct = default)
        => await DbSet.AsNoTracking()
            .Where(n => n.OrgSid.ToLower() == orgSid.ToLower())
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);

    public void Remove(OrgNote note) => DbSet.Remove(note);
}
