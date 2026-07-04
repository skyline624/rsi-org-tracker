using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class TrackedEntityRepository : Repository<TrackedEntity>, ITrackedEntityRepository
{
    public TrackedEntityRepository(TrackerDbContext context) : base(context) { }

    public async Task<TrackedEntity?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(e => e.CitizenId == citizenId, ct);

    public async Task<TrackedEntity?> GetByHandleAsync(string handle, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(e => e.CurrentHandle == handle, ct);
}
