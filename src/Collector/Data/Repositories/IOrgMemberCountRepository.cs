using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrgMemberCountRepository : IRepository<OrgMemberCount>
{
    /// <summary>
    /// Stores the counters unless they equal the organization's latest row. With null
    /// breakdown counts (incomplete read), only <see cref="OrgMemberCount.TotalRows"/> is compared.
    /// Returns whether a row was written.
    /// </summary>
    Task<bool> RecordIfChangedAsync(OrgMemberCount counts, CancellationToken ct = default);
}
