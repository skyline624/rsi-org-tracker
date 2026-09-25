using System.Security.Claims;
using System.Threading.RateLimiting;
using Collector.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Collector.Api.Extensions;

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
    /// Budgets per signed-in user and per anonymous client IP, plus the login policy.
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

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var s = Settings(ctx);
                var window = TimeSpan.FromSeconds(s.WindowSeconds);
                var userId = ctx.User.Identity?.IsAuthenticated == true
                    ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.Identity.Name
                    : null;
                return userId is not null
                    ? FixedWindow($"user:{userId}", s.UserPermitLimit, window)
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
        });
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
