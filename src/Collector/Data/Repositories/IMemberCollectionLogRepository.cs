using Collector.Models;

namespace Collector.Data.Repositories;

public interface IMemberCollectionLogRepository : IRepository<MemberCollectionLog>
{
    Task<MemberCollectionLog?> GetLatestAsync(string orgSid, CancellationToken ct = default);
    Task<IReadOnlyList<MemberCollectionLog>> GetByOrgSidAsync(string orgSid, int limit = 10, CancellationToken ct = default);
    Task<IReadOnlyList<MemberCollectionLog>> GetByCollectionTimeAsync(string orgSid, DateTime collectionTime, CancellationToken ct = default);
}