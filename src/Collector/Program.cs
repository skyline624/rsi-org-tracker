using Collector.Extensions;
using Collector.Options;
using Collector.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

try
{
    // Data directory: COLLECTOR_DATA_DIR, else the data folder above the bin folder.
    var dataDir = DataDirectory.ResolveForCurrentProcess();
    // Same environment the host is about to read (DOTNET_ENVIRONMENT, Production by default).
    DataDirectory.EnsureExistingDatabase(dataDir, production:
        (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production) == Environments.Production);

    // Create data directories before anything else (Serilog needs logs/ to exist)
    Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

    Console.WriteLine("Starting SC-Organizations-Tracker Collector...");

    // Parse flags up-front so we can decide whether to register the hosted
    // services (one-shot CLI modes don't want a long-running loop keeping the host alive).
    var singleRun = args.Contains("--single-run") || args.Contains("-s");
    var integrityCheck = args.Contains("--integrity-check") || args.Contains("-i");
    var skipPhase2 = args.Contains("--skip-phase2");
    var backfillQueue = args.Contains("--backfill-enrichment-queue");
    var repairCorrupted = args.Contains("--repair-corrupted-handles");
    var maintenance = args.Contains("--maintenance");
    var migrateOnly = args.Contains("--migrate");
    var continuousMode = !singleRun && !integrityCheck && !backfillQueue && !repairCorrupted && !maintenance && !migrateOnly;

    // Build host
    var builder = Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        // Fail at startup on a scoped service resolved from the root provider
        // (a DbContext living as long as the process) or an unresolvable registration.
        .UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });

    // Configure services
    builder.ConfigureServices((context, services) =>
    {
        services.AddCollectorServices(context.Configuration, dataDir,
            registerHostedServices: continuousMode, skipPhase2: skipPhase2);
    });

    // Configure logging with absolute path for the file sink
    var logPath = Path.Combine(dataDir, "logs", "collector-.log");
    builder.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.File(logPath,
                rollingInterval: Serilog.RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext();
    });

    var host = builder.Build();

    Console.WriteLine("Host built successfully");

    // `--maintenance verify` only reads, next to a running collector: no bootstrap.
    var verifyOnly = maintenance && args.SkipWhile(a => a != "--maintenance").Skip(1).FirstOrDefault() == "verify";
    if (!verifyOnly)
    {
        await host.Services.EnsureDatabaseAsync(dataDir);
    }

    Console.WriteLine("Database initialized");

    // The unit's ExecStartPre: `systemctl restart sc-collector` returns once tracker.db is
    // migrated (or fails with the migration), before deploy.sh restarts the API on it.
    // Organization names stored HTML-encoded are decoded here, before the first Phase 1
    // compares with them: only their encoding changes, and later runs find none.
    if (migrateOnly)
    {
        using var repairScope = host.Services.CreateScope();
        var decoded = await repairScope.ServiceProvider.GetRequiredService<MaintenanceService>()
            .RepairOrgNamesAsync(dryRun: false);
        Console.WriteLine($"Organization names decoded: {decoded}");
        return;
    }

    var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value;
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Starting SC-Organizations-Tracker Collector");
    logger.LogInformation("Cycle interval: {Interval}", options.CycleInterval);

    // Parse --sample N (default 10)
    var sampleSize = 10;
    var sampleIdx = Array.IndexOf(args, "--sample");
    if (sampleIdx >= 0 && sampleIdx + 1 < args.Length && int.TryParse(args[sampleIdx + 1], out var parsed))
        sampleSize = parsed;

    if (continuousMode)
    {
        // Continuous mode: CollectionWorker (cycle loop) and Phase4Worker run as
        // hosted services; RunAsync returns once SIGTERM / Ctrl+C has stopped them.
        await host.RunAsync();
        logger.LogInformation("Application exiting");
        return;
    }

    // One-shot modes: starting the host installs the SIGTERM / Ctrl+C handlers,
    // which cancel ApplicationStopping.
    await host.StartAsync();
    var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

    if (maintenance)
    {
        // Run with the collector service stopped:
        //   --maintenance measure
        //   --maintenance check
        //   --maintenance purge <target> [--dry-run] [--batch N]
        //   --maintenance repair-org-names [--dry-run]
        // Read-only, also while the collector runs (exit code 2 when a check fails):
        //   --maintenance verify [--since 2026-09-26T05:44]   (UTC, default: 7 days ago)
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MaintenanceService>();
        var verb = args.SkipWhile(a => a != "--maintenance").Skip(1).FirstOrDefault();
        switch (verb)
        {
            case "measure":
                await service.MeasureAsync(DateTime.UtcNow, ct);
                break;
            case "check":
                Console.WriteLine($"quick_check: {await service.QuickCheckAsync(ct)}");
                break;
            case "purge":
                var target = args.SkipWhile(a => a != "purge").Skip(1).FirstOrDefault() ?? "";
                var batchIdx = Array.IndexOf(args, "--batch");
                var batch = batchIdx >= 0 && batchIdx + 1 < args.Length && int.TryParse(args[batchIdx + 1], out var n) ? n : 20_000;
                var report = await service.PurgeAsync(target, args.Contains("--dry-run"), batch, DateTime.UtcNow, ct);
                Console.WriteLine($"{report.Target}: {report.Deleted}/{report.Matched} deleted in {report.Batches} batches"
                    + (report.StoppedBecause != null ? $", stopped: {report.StoppedBecause}" : ""));
                break;
            case "repair-org-names":
                Console.WriteLine($"HTML-encoded organization names: {await service.RepairOrgNamesAsync(args.Contains("--dry-run"), ct)} rows"
                    + (args.Contains("--dry-run") ? " (dry run)" : " decoded"));
                break;
            case "verify":
            {
                var sinceIdx = Array.IndexOf(args, "--since");
                var since = sinceIdx >= 0 && sinceIdx + 1 < args.Length
                    ? DateTime.Parse(args[sinceIdx + 1], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
                    : DateTime.UtcNow.AddDays(-7);
                var results = await scope.ServiceProvider.GetRequiredService<DataVerificationService>().VerifyAsync(since, ct);
                var failed = results.Count(r => r.Failed);
                Console.WriteLine($"verify since {since:u}: {results.Count - failed}/{results.Count} checks pass"
                    + (failed > 0 ? $", {failed} fail: {string.Join(", ", results.Where(r => r.Failed).Select(r => r.Name))}" : ""));
                if (failed > 0) Environment.ExitCode = 2;
                break;
            }
            default:
                Console.WriteLine("usage: --maintenance measure | check | verify [--since <UTC date>] | purge <target> [--dry-run] [--batch N] | repair-org-names [--dry-run]");
                Console.WriteLine($"purge targets: {string.Join(", ", MaintenanceService.TargetNames)}");
                break;
        }
    }
    else if (backfillQueue)
    {
        logger.LogInformation("Running enrichment queue backfill (one-shot)");
        using var scope = host.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<IEnrichmentBackfillService>();
        var inserted = await backfill.BackfillOrphansAsync(ct);
        Console.WriteLine($"Backfill complete: {inserted} handles queued for enrichment.");
    }
    else if (repairCorrupted)
    {
        logger.LogInformation("Running corrupted-handle repair (one-shot)");
        using var scope = host.Services.CreateScope();
        var repair = scope.ServiceProvider.GetRequiredService<ICorruptedUserRepairService>();
        var report = await repair.RepairAsync(ct);
        Console.WriteLine(
            $"Repair complete: scanned={report.Scanned} repaired={report.Repaired} " +
            $"unrecoverable={report.Unrecoverable} requeued={report.Requeued}");
    }
    else if (integrityCheck)
    {
        // Organization listings, then rosters and citizen profiles read since --since
        // (UTC, default 7 days ago), each a sample of N compared with RSI now.
        logger.LogInformation("Running integrity check (sample size: {N})", sampleSize);
        using var scope = host.Services.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IIntegrityCheckService>();
        await checker.RunCheckAsync(sampleSize, ct);
        var sinceIdx = Array.IndexOf(args, "--since");
        var since = sinceIdx >= 0 && sinceIdx + 1 < args.Length
            ? DateTime.Parse(args[sinceIdx + 1], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            : DateTime.UtcNow.AddDays(-7);
        var comparison = scope.ServiceProvider.GetRequiredService<SourceComparisonService>();
        await comparison.CompareRostersAsync(sampleSize, since, ct: ct);
        await comparison.CompareProfilesAsync(sampleSize, since, ct);
    }
    else if (singleRun)
    {
        // Single-run mode: no hosted services registered, just run the cycle and exit.
        logger.LogInformation("Running in single-run mode");
        await host.Services.GetRequiredService<CollectionOrchestrator>().RunSingleCycleAsync(ct, skipPhase2);
    }

    await host.StopAsync();
    logger.LogInformation("Application exiting");
}
catch (Microsoft.Extensions.Hosting.HostAbortedException)
{
    // Rethrow so EF Core design-time tooling can introspect the DbContext.
    throw;
}
catch (OperationCanceledException)
{
    // SIGTERM / Ctrl+C during a one-shot mode.
    Console.WriteLine("Cancelled.");
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL ERROR: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    if (ex.InnerException != null)
    {
        Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
        Console.WriteLine($"Inner stack trace: {ex.InnerException.StackTrace}");
    }
    Environment.Exit(1);
}
