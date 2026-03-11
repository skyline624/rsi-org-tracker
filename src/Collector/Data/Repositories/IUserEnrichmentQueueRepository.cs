using Collector.Models;

namespace Collector.Data.Repositories;

public interface IUserEnrichmentQueueRepository : IRepository<UserEnrichmentQueue>
{
    Task<IReadOnlyList<UserEnrichmentQueue>> GetPendingAsync(int limit = 100, CancellationToken ct = default);
    Task MarkEnrichedAsync(long id, CancellationToken ct = default);
    Task IncrementAttemptAsync(long id, string? error, CancellationToken ct = default);
    Task<bool> IsQueuedAsync(string userHandle, CancellationToken ct = default);
}