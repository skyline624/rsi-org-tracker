using Collector.Models;

namespace Collector.Data.Repositories;

public interface IEntityMembershipRepository : IRepository<EntityMembership>
{
    Task<IReadOnlyList<EntityMembership>> GetByEntityIdAsync(long entityId, CancellationToken ct = default);
    Task<EntityMembership?> GetByEntityAndOrgAsync(long entityId, string orgSid, CancellationToken ct = default);
    void Remove(EntityMembership membership);
}
