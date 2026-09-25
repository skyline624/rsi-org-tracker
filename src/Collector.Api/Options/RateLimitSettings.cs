namespace Collector.Api.Options;

/// <summary>Request budgets, bound to <c>Api:RateLimit</c>.</summary>
public sealed class RateLimitSettings
{
    public const string Section = "Api:RateLimit";

    /// <summary>Length of the fixed window for the per-user and per-IP budgets.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Budget of an authenticated user (partitioned by user id).</summary>
    public int UserPermitLimit { get; set; } = 600;

    /// <summary>Budget of an anonymous client (partitioned by IP).</summary>
    public int AnonymousPermitLimit { get; set; } = 60;

    /// <summary>Extra budget on login and refresh, per client IP (brute force).</summary>
    public LoginLimit Login { get; set; } = new();

    public sealed class LoginLimit
    {
        public int PermitLimit { get; set; } = 10;
        public int WindowSeconds { get; set; } = 300;
    }
}
