using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Collector.Data.Repositories;

/// <summary>
/// Base repository implementation providing common CRUD operations.
/// </summary>
/// <typeparam name="T">Entity type</typeparam>
public abstract class Repository<T> : IRepository<T> where T : class
{
    protected readonly TrackerDbContext Context;
    protected readonly DbSet<T> DbSet;

    protected Repository(TrackerDbContext context)
    {
        Context = context;
        DbSet = context.Set<T>();
    }

    public virtual async Task<T?> GetByIdAsync(long id, CancellationToken ct = default)
        => await DbSet.FindAsync(new object[] { id }, ct);

    public virtual async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
        => await DbSet.ToListAsync(ct);

    public virtual async Task AddAsync(T entity, CancellationToken ct = default)
        => await DbSet.AddAsync(entity, ct);

    public virtual async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default)
        => await DbSet.AddRangeAsync(entities, ct);

    public virtual async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await Context.SaveChangesAsync(ct);

    public virtual async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => await Context.Database.BeginTransactionAsync(ct);
}