using System.Text;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Services;
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

        // Auth — both secrets are mandatory, must meet a minimum entropy floor, and must
        // NOT be the legacy "change-me-…" placeholders. We fail fast at startup rather
        // than silently booting with a weak key.
        var jwtSecret = configuration["Api:JwtSecret"];
        if (string.IsNullOrWhiteSpace(jwtSecret))
            throw new InvalidOperationException(
                "Api:JwtSecret is not configured. Set it via user-secrets " +
                "(dotnet user-secrets set \"Api:JwtSecret\" <value>) or the " +
                "COLLECTOR_API_Api__JwtSecret environment variable.");
        if (jwtSecret.Length < 32)
            throw new InvalidOperationException("Api:JwtSecret must be at least 32 characters (256 bits).");
        if (jwtSecret.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Api:JwtSecret still uses the default placeholder value; rotate it.");

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
                    ctx.Request.Headers.ContainsKey("Authorization")
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
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            })
            .AddScheme<ApiKeySchemeOptions, ApiKeyAuthHandler>("ApiKey", _ => { });

        services.AddAuthorization(opts =>
        {
            opts.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Smart")
                .RequireAuthenticatedUser()
                .Build();
            opts.AddPolicy("AdminOnly",
                policy => policy
                    .AddAuthenticationSchemes("Smart")
                    .RequireAuthenticatedUser()
                    .RequireRole("Admin"));
        });

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUserAccessor>();
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<ActivityLogService>();
        services.AddScoped<StatsService>();

        return services;
    }
}
