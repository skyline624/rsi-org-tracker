using Collector.Models;

namespace Collector.Data.Repositories;

public interface IChangeEventRepository : IRepository<ChangeEvent>
{
    Task<IReadOnlyList<ChangeEvent>> GetByOrgSidAsync(string orgSid, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeEvent>> GetByUserHandleAsync(string userHandle, int limit = 100, CancellationToken ct = default);

    /// <summary>Deletes the <c>member_left</c> events of a handle in an org from <paramref name="since"/> on.</summary>
    Task<int> DeleteDeparturesSinceAsync(string orgSid, string userHandle, DateTime since, CancellationToken ct = default);
}