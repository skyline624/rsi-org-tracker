using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class TrackedEntityRepository : Repository<TrackedEntity>, ITrackedEntityRepository
{
    public TrackedEntityRepository(TrackerDbContext context) : base(context) { }

    public async Task<TrackedEntity?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(e => e.CitizenId == citizenId, ct);

    public async Task<TrackedEntity?> GetByHandleAsync(string handle, CancellationToken ct = default)
        // Case-insensitive: handles are matched regardless of capitalisation, so a
        // person is resolved (and not duplicated) whether accessed as "zeno1" or "Zeno1".
        => await DbSet.FirstOrDefaultAsync(
            e => e.CurrentHandle != null && e.CurrentHandle.ToLower() == handle.ToLower(), ct);
}
