using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Collector.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Collector.Api.Auth;

namespace Collector.Api.Extensions;

/// <summary>Registers the global and endpoint-specific fixed-window request budgets.</summary>
public static class RateLimitingExtensions
{
    /// <summary>Stricter per-IP budget for login (credential stuffing).</summary>
    public const string LoginPolicy = "login";

    /// <summary>
    /// The same budget for refresh, counted apart: failed logins from an address must
    /// not stop the sessions of everyone else behind it from being renewed.
    /// </summary>
    public const string RefreshPolicy = "refresh";

    /// <summary>
    /// Extra budget for Discord roster writes, per key owner after authentication.
    /// </summary>
    public const string DiscordIngestPolicy = "discord-ingest";

    /// <summary>
    /// Budgets per signed-in user and per anonymous client IP, plus login, refresh and
    /// Discord ingestion policies. Rejected requests say when to retry.
    /// The IP is the real client address: X-Forwarded-For is only honoured from the
    /// loopback proxy (see UseForwardedHeaders configuration in Program.cs). Must run
    /// after authentication so signed-in users get their own budget.
    /// </summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitSettings>().Bind(configuration.GetSection(RateLimitSettings.Section));

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers["Retry-After"] =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var s = Settings(ctx);
                var window = TimeSpan.FromSeconds(s.WindowSeconds);
                var userId = ctx.User.Identity?.IsAuthenticated == true
                    ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.Identity.Name
                    : null;
                var scoped = ctx.User.HasClaim(DiscordIngestAuth.ScopeClaimType, DiscordIngestAuth.IngestScope);
                return userId is not null
                    ? FixedWindow(scoped ? $"discord:ingest:user:{userId}" : $"user:{userId}", s.UserPermitLimit, window)
                    : FixedWindow($"ip:{ClientIp(ctx)}", s.AnonymousPermitLimit, window);
            });

            options.AddPolicy(LoginPolicy, ctx =>
            {
                var login = Settings(ctx).Login;
                return FixedWindow($"login:{ClientIp(ctx)}", login.PermitLimit, TimeSpan.FromSeconds(login.WindowSeconds));
            });

            options.AddPolicy(RefreshPolicy, ctx =>
            {
                var login = Settings(ctx).Login;
                return FixedWindow($"refresh:{ClientIp(ctx)}", login.PermitLimit, TimeSpan.FromSeconds(login.WindowSeconds));
            });

            options.AddPolicy(DiscordIngestPolicy, ctx =>
            {
                var ingest = Settings(ctx).DiscordIngest;
                return FixedWindow(DiscordIngestPartitionKey(ctx), ingest.PermitLimit, TimeSpan.FromSeconds(ingest.WindowSeconds));
            });
        });
    }

    /// <summary>Keys issued to one owner share a budget; unauthenticated callers share only their IP's budget.</summary>
    public static string DiscordIngestPartitionKey(HttpContext ctx)
    {
        var userId = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
        return userId is not null
            ? $"discord-ingest:user:{userId}"
            : $"discord-ingest:ip:{ClientIp(ctx)}";
    }

    private static RateLimitSettings Settings(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value;

    private static string ClientIp(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
        });
}
