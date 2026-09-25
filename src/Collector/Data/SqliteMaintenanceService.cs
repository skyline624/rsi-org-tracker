using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Collector.Data;

/// <summary>
/// Runs <c>PRAGMA optimize</c> once a day so the query planner's statistics follow
/// the tables' growth.
/// </summary>
public sealed class SqliteMaintenanceService : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromDays(1);

    // analysis_limit caps ANALYZE at ~400 rows per index: ANALYZE writes
    // sqlite_stat1, so an unbounded run on a 12M-row table would hold the write
    // lock for minutes. 0x10002 = analyze where useful (0x02), checking every
    // table and not only those this fresh connection has queried (0x10000).
    private const string OptimizeSql = "PRAGMA analysis_limit = 400; PRAGMA optimize = 0x10002;";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SqliteMaintenanceService> _logger;

    public SqliteMaintenanceService(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<SqliteMaintenanceService> logger)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await OptimizeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task OptimizeAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            await db.Database.ExecuteSqlRawAsync(OptimizeSql, ct);
            _logger.LogInformation("SQLite PRAGMA optimize done");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SQLite PRAGMA optimize failed; next attempt in {Period}", Period);
        }
    }
}
