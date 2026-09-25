using System.Net;
using Collector.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>
/// deploy.sh rolls back when /api/health/ready fails. The collector migrates tracker.db,
/// so a release deployed without restarting it must not be reported ready.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReadinessTests(ApiFactory factory)
{
    [Fact]
    public async Task Ready_WhenTrackerDbIsMigrated()
    {
        (await factory.CreateClient().GetAsync("/api/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NotReady_WhileATrackerDbMigrationIsPending()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var last = (await db.Database.GetAppliedMigrationsAsync()).Last();
        var productVersion = await db.Database
            .SqlQueryRaw<string>("SELECT ProductVersion AS Value FROM __EFMigrationsHistory WHERE MigrationId = {0}", last)
            .SingleAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}", last);
        try
        {
            var response = await factory.CreateClient().GetAsync("/api/health/ready");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({0}, {1})", last, productVersion);
        }
    }
}
