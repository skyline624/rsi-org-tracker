using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.Extensions.Logging;

namespace Collector.Services;

public interface IEntityResolver
{
    /// <summary>
    /// Resolves the <see cref="TrackedEntity"/> for a (citizenId, handle) pair, creating a
    /// new one if none matches. Returns the entity id. Best-effort: keeps CurrentHandle /
    /// DisplayName fresh for collected entities and never mutates manual entries.
    /// </summary>
    Task<long> ResolveOrCreateAsync(int? citizenId, string? handle, string? displayName = null, CancellationToken ct = default);
}

/// <summary>
/// Maps RSI identities (citizen id and/or handle, which can change over time) onto the
/// tool's stable internal <see cref="TrackedEntity"/>. Resolution order: citizen id →
/// current handle → handle history (renamed handle) → create.
/// </summary>
public class EntityResolver : IEntityResolver
{
    private readonly ITrackedEntityRepository _repo;
    private readonly IUserHandleHistoryRepository _handleHistory;
    private readonly ILogger<EntityResolver> _logger;

    public EntityResolver(
        ITrackedEntityRepository repo,
        IUserHandleHistoryRepository handleHistory,
        ILogger<EntityResolver> logger)
    {
        _repo = repo;
        _handleHistory = handleHistory;
        _logger = logger;
    }

    public async Task<long> ResolveOrCreateAsync(
        int? citizenId, string? handle, string? displayName = null, CancellationToken ct = default)
    {
        handle = string.IsNullOrWhiteSpace(handle) ? null : handle.Trim();

        if (citizenId is null && handle is null)
            throw new ArgumentException("ResolveOrCreateAsync requires at least a citizenId or a handle.");

        // 1) Stable identity: resolve by citizen id when known.
        if (citizenId is int cid)
        {
            var byCid = await _repo.GetByCitizenIdAsync(cid, ct);
            if (byCid is not null)
                return await TouchAsync(byCid, handle, displayName, ct);

            // An entity may have been created earlier from the handle alone (before the
            // citizen id was known) — adopt it instead of forking a duplicate.
            if (handle is not null)
            {
                var byHandle = await _repo.GetByHandleAsync(handle, ct);
                if (byHandle is not null
                    && byHandle.CitizenId is null
                    && byHandle.Source != TrackedEntitySource.Manual)
                {
                    byHandle.CitizenId = cid;
                    return await TouchAsync(byHandle, handle, displayName, ct);
                }
            }

            return await CreateAsync(cid, handle, displayName, ct);
        }

        // 2) No citizen id: resolve by current handle.
        var current = await _repo.GetByHandleAsync(handle!, ct);
        if (current is not null)
            return await TouchAsync(current, handle, displayName, ct);

        // The handle may have belonged to a citizen we already track (a rename). Follow
        // handle history → citizen id → entity before giving up and creating a new one.
        var hist = await _handleHistory.GetByHandleAsync(handle!, ct);
        if (hist is not null)
        {
            var byCid = await _repo.GetByCitizenIdAsync(hist.CitizenId, ct);
            if (byCid is not null)
                return await TouchAsync(byCid, handle, displayName, ct);
        }

        return await CreateAsync(null, handle, displayName, ct);
    }

    private async Task<long> TouchAsync(TrackedEntity e, string? handle, string? displayName, CancellationToken ct)
    {
        // Manual entries are curated by an admin — never overwrite them from collection.
        if (e.Source == TrackedEntitySource.Manual)
            return e.Id;

        var changed = false;
        if (handle is not null && !string.Equals(e.CurrentHandle, handle, StringComparison.Ordinal))
        {
            e.CurrentHandle = handle;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(displayName) && e.DisplayName != displayName)
        {
            e.DisplayName = displayName;
            changed = true;
        }
        if (changed)
        {
            e.UpdatedAt = DateTime.UtcNow;
            await _repo.SaveChangesAsync(ct);
        }
        return e.Id;
    }

    private async Task<long> CreateAsync(int? citizenId, string? handle, string? displayName, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var entity = new TrackedEntity
        {
            CitizenId = citizenId,
            CurrentHandle = handle,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            Source = TrackedEntitySource.Collected,
            Status = TrackedEntityStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repo.AddAsync(entity, ct);
        await _repo.SaveChangesAsync(ct);
        return entity.Id;
    }
}
