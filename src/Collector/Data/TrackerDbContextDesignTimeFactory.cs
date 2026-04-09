using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Collector.Data;

/// <summary>
/// Design-time factory used by `dotnet ef` tooling for TrackerDbContext.
/// </summary>
public class TrackerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TrackerDbContext>
{
    public TrackerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite("Data Source=_designtime_tracker.db")
            .Options;
        return new TrackerDbContext(options);
    }
}
