using Collector.Api.Dtos.Common;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Extensions;

/// <summary>
/// Uniform pagination helper. Replaces the ad-hoc <c>Skip/Take + CountAsync + ToListAsync</c>
/// blocks copy-pasted into every controller.
/// </summary>
public static class PaginationExtensions
{
    /// <summary>
    /// Executes the query with pagination and projects each row via <paramref name="projector"/>.
    /// </summary>
    public static async Task<PaginatedResponse<TDto>> ToPaginatedAsync<TEntity, TDto>(
        this IQueryable<TEntity> query,
        int page,
        int pageSize,
        Func<TEntity, TDto> projector,
        CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 500 ? 50 : pageSize;

        var total = await query.CountAsync(ct);
        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PaginatedResponse<TDto>
        {
            Items = rows.Select(projector).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }
}
