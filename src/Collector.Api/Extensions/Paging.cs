namespace Collector.Api.Extensions;

/// <summary>
/// Bounds of the paging query parameters. Out-of-range values are brought back into
/// range: a negative page used to make Skip throw, and a huge limit dumped whole tables.
/// </summary>
public static class Paging
{
    public const int MaxLimit = 500;
    public const int MaxPageSize = 200;
    public const int MaxDays = 365;

    public static int Page(int page) => Math.Max(page, 1);

    public static int PageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);

    /// <summary>Offset for already bounded paging values, without overflowing for a huge page.</summary>
    public static int Offset(int page, int pageSize) => (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize);

    public static int Limit(int limit) => Math.Clamp(limit, 1, MaxLimit);

    public static int Days(int days) => Math.Clamp(days, 1, MaxDays);
}
