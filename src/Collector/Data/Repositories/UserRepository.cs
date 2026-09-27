using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data.Repositories;

public class UserRepository : Repository<User>, IUserRepository
{
    public UserRepository(TrackerDbContext context) : base(context) { }

    public async Task<User?> GetByCitizenIdAsync(int citizenId, CancellationToken ct = default)
    {
        return await DbSet.FirstOrDefaultAsync(u => u.CitizenId == citizenId, ct);
    }

    public async Task<User?> GetByHandleAsync(string handle, CancellationToken ct = default)
    {
        // A handle given up and taken by another citizen is held by two rows until the
        // former owner is read again: the one whose profile was read last holds it now.
        return await DbSet
            .Where(u => u.UserHandle == handle)
            .OrderByDescending(u => u.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Dictionary<string, int>> GetCitizenIdsByHandlesAsync(
        IReadOnlyCollection<string> handles, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (handles.Count == 0) return result;

        var current = await DbSet.AsNoTracking()
            .Where(u => handles.Contains(u.UserHandle))
            .Select(u => new { u.UserHandle, u.CitizenId, u.UpdatedAt })
            .ToListAsync(ct);
        foreach (var row in current.OrderByDescending(r => r.UpdatedAt))
        {
            result.TryAdd(row.UserHandle, row.CitizenId);
        }

        var former = await Context.UserHandleHistories.AsNoTracking()
            .Where(h => handles.Contains(h.UserHandle))
            .Select(h => new { h.UserHandle, h.CitizenId, h.LastSeen })
            .ToListAsync(ct);
        foreach (var row in former.OrderByDescending(r => r.LastSeen))
        {
            result.TryAdd(row.UserHandle, row.CitizenId);
        }

        return result;
    }

    /// <summary>
    /// Citizens after the cursor read by an older parser, walking the primary key from the
    /// cursor: rows already read again are skipped without an index of their own.
    /// </summary>
    public static IQueryable<User> ProfilesToRefresh(IQueryable<User> source, long afterId, int version)
        => source.Where(u => u.Id > afterId && u.ParserVersion < version).OrderBy(u => u.Id);

    public async Task<IReadOnlyList<ProfileToRefresh>> GetProfilesToRefreshAsync(
        long afterId, int version, int limit, CancellationToken ct = default)
    {
        return await ProfilesToRefresh(DbSet.AsNoTracking(), afterId, version)
            .Take(limit)
            .Select(u => new ProfileToRefresh(u.Id, u.CitizenId, u.UserHandle))
            .ToListAsync(ct);
    }

    public async Task MarkProfileReadAsync(long id, int version, CancellationToken ct = default)
    {
        // UpdatedAt is left alone: nothing of the profile changed, and a handle held by two
        // rows goes to the one read last (GetByHandleAsync).
        await DbSet.Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ParserVersion, version), ct);
    }
}