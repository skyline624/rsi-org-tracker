using Collector.Data;
using Collector.Data.Repositories;
using Collector.Options;
using Collector.Parsers;
using Collector.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Collector.Extensions;

/// <summary>
/// Extension methods for configuring services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds only the data layer (TrackerDbContext + repositories) without HTTP clients or collection services.
    /// Used by Collector.Api to share the data layer without pulling in collection dependencies.
    /// </summary>
    public static IServiceCollection AddCollectorDataServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "tracker.db");
        services.AddDbContext<TrackerDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IDiscoveredOrganizationRepository, DiscoveredOrganizationRepository>();
        services.AddScoped<IOrganizationMemberRepository, OrganizationMemberRepository>();
        services.AddScoped<IMemberCollectionLogRepository, MemberCollectionLogRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserHandleHistoryRepository, UserHandleHistoryRepository>();
        services.AddScoped<IUserEnrichmentQueueRepository, UserEnrichmentQueueRepository>();
        services.AddScoped<IChangeEventRepository, ChangeEventRepository>();

        return services;
    }

    /// <summary>
    /// Adds all collector services to the DI container.
    /// </summary>
    public static IServiceCollection AddCollectorServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string dataDir)
    {
        // Configure options
        services.Configure<CollectorOptions>(
            configuration.GetSection("Collector"));

        // Database + repositories
        services.AddCollectorDataServices(configuration, dataDir);

        // Parsers
        services.AddSingleton<OrganizationHtmlParser>();
        services.AddSingleton<OrgPageHtmlParser>();
        services.AddSingleton<MemberHtmlParser>();
        services.AddSingleton<UserProfileHtmlParser>();

        // HTTP Client with Polly
        services.AddHttpClient<IRsiApiClient, RsiApiClient>(client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "SC-Organizations-Tracker/2.0");
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        // Services
        services.AddScoped<IOrganizationCollector, OrganizationCollector>();
        services.AddScoped<IMemberCollector, MemberCollector>();
        services.AddScoped<IUserCollector, UserCollector>();
        services.AddScoped<IChangeDetector, ChangeDetector>();
        services.AddScoped<IUserChangeDetector, UserChangeDetector>();

        // Orchestrator (Scoped because it injects Scoped services)
        services.AddScoped<CollectionOrchestrator>();

        // Integrity check
        services.AddScoped<IIntegrityCheckService, IntegrityCheckService>();

        return services;
    }

    /// <summary>
    /// Creates and ensures the database exists.
    /// </summary>
    public static async Task EnsureDatabaseAsync(this IServiceProvider serviceProvider, string dataDir)
    {
        Directory.CreateDirectory(dataDir);

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }
}
