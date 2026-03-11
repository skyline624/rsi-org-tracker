using Collector.Extensions;
using Collector.Options;
using Collector.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

try
{
    Console.WriteLine("Starting SC-Organizations-Tracker Collector...");

    // Build host
    var builder = Host.CreateDefaultBuilder(args);

    // Configure services
    builder.ConfigureServices((context, services) =>
    {
        services.AddCollectorServices(context.Configuration);
    });

    // Configure logging
    builder.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext();
    });

    var host = builder.Build();

    Console.WriteLine("Host built successfully");

    // Ensure database exists
    await host.Services.EnsureDatabaseAsync();

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

    if (singleRun)
    {
        logger.LogInformation("Running in single-run mode");
        await orchestrator.RunSingleCycleAsync(ct);
    }
    else
    {
        await orchestrator.RunCollectionLoopAsync(ct);
    }

    logger.LogInformation("Application exiting");
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