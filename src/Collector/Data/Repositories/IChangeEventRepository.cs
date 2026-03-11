using Collector.Models;

namespace Collector.Data.Repositories;

public interface IChangeEventRepository : IRepository<ChangeEvent>
{
    Task<IReadOnlyList<ChangeEvent>> GetByOrgSidAsync(string orgSid, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeEvent>> GetByUserHandleAsync(string userHandle, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeEvent>> GetRecentAsync(int limit = 100, CancellationToken ct = default);
}