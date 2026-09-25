using System.Diagnostics;
using Collector.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Collector.Tests;

/// <summary>
/// <c>Collector --migrate</c> is the collector unit's ExecStartPre: it must apply the
/// tracker.db migrations and exit, so that <c>systemctl restart sc-collector</c> returns
/// once the schema is ready and deploy.sh can restart the API on it.
/// </summary>
public sealed class MigrateModeTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "sc-tracker-migrate-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MigrateMode_AppliesTheMigrationsAndExits()
    {
        var (exited, exitCode, output) = await RunMigrateAsync("Development");

        exited.Should().BeTrue("--migrate must exit instead of starting the collection loop");
        exitCode.Should().Be(0, output);
        await using var db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dataDir, "tracker.db")};Pooling=False").Options);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InProduction_AMissingDatabaseStopsTheCollector_InsteadOfStartingAnEmptyOne()
    {
        var (exited, exitCode, output) = await RunMigrateAsync("Production");

        exited.Should().BeTrue();
        exitCode.Should().NotBe(0, output);
        File.Exists(Path.Combine(_dataDir, "tracker.db")).Should().BeFalse();
    }

    private async Task<(bool Exited, int ExitCode, string Output)> RunMigrateAsync(string environment)
    {
        Directory.CreateDirectory(_dataDir);
        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            $"\"{Path.Combine(AppContext.BaseDirectory, "Collector.dll")}\" --migrate")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["COLLECTOR_DATA_DIR"] = _dataDir;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        // Should the mode be missing, the collection loop must not reach RSI from a test.
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:9";
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:9";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        if (!exited) process.Kill(entireProcessTree: true);
        return (exited, exited ? process.ExitCode : -1, await output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
