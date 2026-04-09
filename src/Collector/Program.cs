using Collector.Extensions;
using Collector.Options;
using Collector.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

try
{
    // Data directory is always at project root (one level above the bin folder)
    var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../..", "data"));

    // Create data directories before anything else (Serilog needs logs/ to exist)
    Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

    Console.WriteLine("Starting SC-Organizations-Tracker Collector...");

    // Build host
    var builder = Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory);

    // Configure services
    builder.ConfigureServices((context, services) =>
    {
        services.AddCollectorServices(context.Configuration, dataDir);
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

    // Get orchestrator
    var orchestrator = host.Services.GetRequiredService<CollectionOrchestrator>();
    var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value;

    // Create cancellation token
    using var cts = new CancellationTokenSource();
    var ct = cts.Token;

    // Handle Ctrl+C
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        Console.WriteLine("Shutdown requested. Finishing current operation...");
    };

    // Run the collection loop
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Starting SC-Organizations-Tracker Collector");
    logger.LogInformation("Cycle interval: {Interval}", options.CycleInterval);

    // Check for single run mode
    var singleRun = args.Contains("--single-run") || args.Contains("-s");
    var integrityCheck = args.Contains("--integrity-check") || args.Contains("-i");
    var skipPhase2 = args.Contains("--skip-phase2");

    // Parse --sample N (default 10)
    var sampleSize = 10;
    var sampleIdx = Array.IndexOf(args, "--sample");
    if (sampleIdx >= 0 && sampleIdx + 1 < args.Length && int.TryParse(args[sampleIdx + 1], out var parsed))
        sampleSize = parsed;

    if (integrityCheck)
    {
        logger.LogInformation("Running integrity check (sample size: {N})", sampleSize);
        using var scope = host.Services.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IIntegrityCheckService>();
        await checker.RunCheckAsync(sampleSize, ct);
    }
    else if (singleRun)
    {
        logger.LogInformation("Running in single-run mode");
        await orchestrator.RunSingleCycleAsync(ct, skipPhase2);
    }
    else
    {
        await orchestrator.RunCollectionLoopAsync(ct, skipPhase2);
    }

    logger.LogInformation("Application exiting");
}
catch (Microsoft.Extensions.Hosting.HostAbortedException)
{
    // Rethrow so EF Core design-time tooling can introspect the DbContext.
    throw;
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
