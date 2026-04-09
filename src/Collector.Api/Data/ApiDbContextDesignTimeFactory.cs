using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Collector.Api.Data;

/// <summary>
/// Design-time factory used by `dotnet ef` tooling (migrations, scaffolding).
/// At runtime the real factory lives in Program.cs via DI, so this file only
/// matters for the CLI workflow.
/// </summary>
public class ApiDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ApiDbContext>
{
    public ApiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApiDbContext>()
            .UseSqlite("Data Source=_designtime_api.db")
            .Options;
        return new ApiDbContext(options);
    }
}
