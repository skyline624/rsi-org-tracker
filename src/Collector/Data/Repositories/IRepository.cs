using Microsoft.EntityFrameworkCore.Storage;

namespace Collector.Data.Repositories;

/// <summary>
/// Generic repository interface for basic CRUD operations.
/// </summary>
/// <typeparam name="T">Entity type</typeparam>
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops all tracked entities from the underlying DbContext. Use this after a rollback
    /// to prevent dirty in-memory state from leaking into subsequent calls that share
    /// the same scoped DbContext.
    /// </summary>
    void ClearTrackedEntities();
}