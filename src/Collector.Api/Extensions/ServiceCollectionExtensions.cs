using System.Text;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Services;
using Collector.Api.Services.Discord;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Collector.Api.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers API-specific services: ApiDbContext, auth, JWT, services.
    /// </summary>
    public static IServiceCollection AddApiServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string dataDir)
    {
        // API database (separate file to avoid EnsureCreated conflicts with tracker.db)
        var dbPath = Path.Combine(dataDir, "api.db");
        services.AddDbContext<ApiDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // Auth — the RSA signing key and the admin key are mandatory, must meet a minimum
        // strength floor, and the admin key must NOT be a legacy "change-me-…" placeholder.
        // We fail fast at startup rather than silently booting with a weak key.
        var jwtKeys = JwtKeyProvider.FromConfiguration(configuration);
        services.AddSingleton(jwtKeys);

        var adminKey = configuration["Api:AdminApiKey"];
        if (string.IsNullOrWhiteSpace(adminKey))
            throw new InvalidOperationException(
                "Api:AdminApiKey is not configured. Set it via user-secrets or " +
                "the COLLECTOR_API_Api__AdminApiKey environment variable.");
        if (adminKey.Length < 24)
            throw new InvalidOperationException("Api:AdminApiKey must be at least 24 characters.");
        if (adminKey.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Api:AdminApiKey still uses the default placeholder value; rotate it.");

        var issuer = configuration["Api:JwtIssuer"] ?? "sc-tracker-api";
        var audience = configuration["Api:JwtAudience"] ?? "sc-tracker-clients";

        services.AddAuthentication("Smart")
            .AddPolicyScheme("Smart", "JWT or ApiKey", opts =>
                opts.ForwardDefaultSelector = ctx =>
                    ctx.Request.Path.StartsWithSegments(DiscordIngestAuth.PathPrefix, StringComparison.OrdinalIgnoreCase)
                        ? DiscordIngestAuth.SchemeName
                        : ctx.Request.Path.StartsWithSegments(BotReadAuth.PathPrefix, StringComparison.OrdinalIgnoreCase)
                            ? BotReadAuth.SchemeName
                            : ctx.Request.Headers.ContainsKey("Authorization")
                                ? JwtBearerDefaults.AuthenticationScheme
                                : "ApiKey")
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = jwtKeys.PublicKey,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            })
            .AddScheme<ApiKeySchemeOptions, ApiKeyAuthHandler>("ApiKey", _ => { })
            .AddScheme<ApiKeySchemeOptions, DiscordIngestKeyAuthHandler>(DiscordIngestAuth.SchemeName, _ => { })
            .AddScheme<ApiKeySchemeOptions, BotReadKeyAuthHandler>(BotReadAuth.SchemeName, _ => { });

        services.AddAuthorization(opts =>
        {
            opts.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Smart")
                .RequireAuthenticatedUser()
                .Build();
            // Private site: an endpoint without [Authorize] is still authenticated;
            // anonymous ones opt out explicitly with [AllowAnonymous].
            opts.FallbackPolicy = opts.DefaultPolicy;
            opts.AddPolicy(DiscordIngestAuth.PolicyName,
                policy => policy
                    .AddAuthenticationSchemes(DiscordIngestAuth.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(DiscordIngestAuth.ScopeClaimType, DiscordIngestAuth.IngestScope));
            opts.AddPolicy(BotReadAuth.PolicyName,
                policy => policy
                    .AddAuthenticationSchemes(BotReadAuth.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(BotReadAuth.ScopeClaimType, BotReadAuth.Scope));
            opts.AddPolicy("AdminOnly",
                policy => policy
                    .AddAuthenticationSchemes("Smart")
                    .RequireAuthenticatedUser()
                    .RequireRole("Admin"));
        });

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUserAccessor>();
        services.AddScoped<TokenService>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<AuthService>();
        services.AddHostedService<RefreshTokenCleanupService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<ActivityLogService>();
        services.AddMemoryCache();
        services.AddScoped<StatsService>();
        services.AddScoped<UserLookupService>();
        services.AddScoped<OrganizationLookupService>();
        services.AddScoped<BotRequestFilter>();

        // Audio files are stored under <dataDir>/audio (outside the DB).
        services.AddSingleton(new AudioStorageService(Path.Combine(dataDir, "audio")));
        services.AddHostedService<AudioOrphanSweeper>();
        services.AddOptions<Collector.Api.Options.AudioSettings>().Bind(configuration.GetSection(Collector.Api.Options.AudioSettings.Section));

        // Discord rosters: plugin settings shown to users (Discord:Ingest) and retention (Discord:Retention).
        services.AddOptions<Collector.Api.Options.DiscordOptions>().Bind(configuration.GetSection(Collector.Api.Options.DiscordOptions.Section));

        services.AddSingleton<DiscordWriteGate>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DiscordIngestGateFilter>();
        services.AddScoped<DiscordIngestService>();
        services.AddScoped<DiscordRosterQueryService>();
        services.AddScoped<DiscordReconciliationService>();
        services.AddScoped<DiscordHandleLookup>();
        services.AddScoped<DiscordSuggestionService>();
        services.AddSingleton<DiscordAutoLinkQueue>();
        services.AddHostedService<DiscordAutoLinkService>();
        services.AddScoped<DiscordProfileService>();
        services.AddScoped<DiscordGuildConfigService>();
        services.AddHostedService<DiscordRetentionService>();

        return services;
    }
}
