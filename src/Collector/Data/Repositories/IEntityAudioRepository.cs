using Collector.Models;

namespace Collector.Data.Repositories;

public interface IEntityAudioRepository : IRepository<EntityAudio>
{
    Task<IReadOnlyList<EntityAudio>> GetByEntityIdAsync(long entityId, CancellationToken ct = default);
    void Remove(EntityAudio audio);

    /// <summary>Total size of the recordings uploaded by an account (quota).</summary>
    Task<long> GetTotalBytesByAuthorAsync(long authorApiUserId, CancellationToken ct = default);

    /// <summary>Every stored relative path (used to find files without a row).</summary>
    Task<IReadOnlySet<string>> GetAllStoredPathsAsync(CancellationToken ct = default);
}
