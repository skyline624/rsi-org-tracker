using Collector.Models;

namespace Collector.Data.Repositories;

public interface IOrgNoteRepository : IRepository<OrgNote>
{
    Task<IReadOnlyList<OrgNote>> GetByOrgSidAsync(string orgSid, CancellationToken ct = default);
    void Remove(OrgNote note);
}
