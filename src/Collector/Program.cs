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
    var continuousMode = !singleRun && !integrityCheck && !backfillQueue && !repairCorrupted;

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

    // Ensure database exists
    await host.Services.EnsureDatabaseAsync(dataDir);

    Console.WriteLine("Database initialized");

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

    if (backfillQueue)
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
        logger.LogInformation("Running integrity check (sample size: {N})", sampleSize);
        using var scope = host.Services.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IIntegrityCheckService>();
        await checker.RunCheckAsync(sampleSize, ct);
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
