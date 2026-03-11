using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class DiscoveredOrganizationRepository : Repository<DiscoveredOrganization>, IDiscoveredOrganizationRepository
{
    public DiscoveredOrganizationRepository(TrackerDbContext context) : base(context) { }

    public async Task<bool> ExistsAsync(string sid, CancellationToken ct = default)
    {
        return await DbSet.AnyAsync(d => d.Sid == sid, ct);
    }

    public async Task<IReadOnlyList<DiscoveredOrganization>> GetAllUnprocessedAsync(CancellationToken ct = default)
    {
        // All discovered orgs are considered unprocessed until metadata is collected
        return await DbSet.ToListAsync(ct);
    }

    public async Task MarkProcessedAsync(string sid, CancellationToken ct = default)
    {
        // In the current design, we don't need to mark processed
        // The presence in organizations table indicates processing
        await Task.CompletedTask;
    }
}