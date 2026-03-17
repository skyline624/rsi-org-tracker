namespace Collector.Api.Dtos.Common;

public class PaginatedResponse<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)Total / PageSize) : 0;

    // Use when total count comes from DB (efficient)
    public static PaginatedResponse<T> Create(IReadOnlyList<T> items, int page, int pageSize, int total) => new()
    {
        Items = items,
        Total = total,
        Page = page,
        PageSize = pageSize,
    };

    // Use when all items are already in memory
    public static PaginatedResponse<T> Create(IEnumerable<T> source, int page, int pageSize)
    {
        var list = source.ToList();
        var items = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PaginatedResponse<T> { Items = items, Total = list.Count, Page = page, PageSize = pageSize };
    }
}
