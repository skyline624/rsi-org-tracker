using Collector.Data;
using Collector.Data.Repositories;
using Collector.Options;
using Collector.Parsers;
using Collector.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        // Configure and validate options at startup (fail-fast if any field is wrong).
        services.AddOptions<CollectorOptions>()
            .Bind(configuration.GetSection("Collector"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CollectorOptions>, CollectorOptionsValidator>();

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

        // Enrichment queue backfill (one-shot repair)
        services.AddScoped<IEnrichmentBackfillService, EnrichmentBackfillService>();

        return services;
    }

    /// <summary>
    /// Creates directories and bootstraps the tracker database via EF Core migrations.
    /// </summary>
    public static async Task EnsureDatabaseAsync(this IServiceProvider serviceProvider, string dataDir)
    {
        Directory.CreateDirectory(dataDir);

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        var logger = scope.ServiceProvider
            .GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ?.CreateLogger("DatabaseBootstrap");

        await Collector.Data.DatabaseBootstrap.MigrateOrAdoptAsync(dbContext, "organizations", logger);

        // One-shot deduplication: remove duplicate pending queue entries keeping the oldest per handle
        await dbContext.Database.ExecuteSqlRawAsync(@"
            DELETE FROM user_enrichment_queue
            WHERE Id NOT IN (
                SELECT MIN(Id) FROM user_enrichment_queue
                WHERE Enriched = 0
                GROUP BY UserHandle
            )
            AND Enriched = 0;
        ");

        // Ensure partial unique index exists (legacy databases built before this index was added)
        await dbContext.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_user_enrichment_queue_UserHandle_Pending
            ON user_enrichment_queue (UserHandle)
            WHERE Enriched = 0;
        ");

        // Tombstone columns on discovered_organizations (added without an EF
        // migration; SQLite ALTER TABLE ADD COLUMN throws if they exist already
        // so we gate on PRAGMA table_info first).
        await EnsureDiscoveredOrgTombstoneColumnsAsync(dbContext);
    }

    private static async Task EnsureDiscoveredOrgTombstoneColumnsAsync(Data.TrackerDbContext dbContext)
    {
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)dbContext.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(discovered_organizations);";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existing.Add(reader.GetString(1));
                }
            }

            if (!existing.Contains("ConsecutiveNotFoundCount"))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = @"ALTER TABLE discovered_organizations
                    ADD COLUMN ConsecutiveNotFoundCount INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync();
            }

            if (!existing.Contains("DeadAt"))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = @"ALTER TABLE discovered_organizations
                    ADD COLUMN DeadAt TEXT NULL;";
                await alter.ExecuteNonQueryAsync();
            }

            await using (var idx = conn.CreateCommand())
            {
                idx.CommandText = @"CREATE INDEX IF NOT EXISTS IX_discovered_organizations_DeadAt
                    ON discovered_organizations (DeadAt);";
                await idx.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }
}
