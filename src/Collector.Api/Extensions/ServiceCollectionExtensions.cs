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

        // Auth
        var jwtSecret = configuration["Api:JwtSecret"]
            ?? throw new InvalidOperationException("Api:JwtSecret is required");
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
