using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class UserEnrichmentQueueRepository : Repository<UserEnrichmentQueue>, IUserEnrichmentQueueRepository
{
    public UserEnrichmentQueueRepository(TrackerDbContext context) : base(context) { }

    public async Task<IReadOnlyList<UserEnrichmentQueue>> GetPendingAsync(int limit = 100, int maxAttempts = int.MaxValue, CancellationToken ct = default)
    {
        return await DbSet
            .Where(q => !q.Enriched && q.AttemptCount < maxAttempts)
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.QueuedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task MarkEnrichedAsync(long id, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            item.Enriched = true;
            item.EnrichedAt = DateTime.UtcNow;
            await Context.SaveChangesAsync(ct);
        }
    }

    public async Task IncrementAttemptAsync(long id, string? error, CancellationToken ct = default)
    {
        var item = await DbSet.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            item.AttemptCount++;
            item.LastError = error;
            await Context.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> IsQueuedAsync(string userHandle, CancellationToken ct = default)
    {
        return await DbSet.AnyAsync(q => q.UserHandle == userHandle && !q.Enriched, ct);
    }
}