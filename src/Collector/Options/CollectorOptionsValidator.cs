using Microsoft.Extensions.Options;

namespace Collector.Options;

/// <summary>
/// Fail-fast validator for <see cref="CollectorOptions"/>. Registered with
/// <c>.ValidateOnStart()</c> so a mis-configured host never gets past Host.Build().
/// </summary>
public class CollectorOptionsValidator : IValidateOptions<CollectorOptions>
{
    public ValidateOptionsResult Validate(string? name, CollectorOptions options)
    {
        var failures = new List<string>();

        if (options.CycleInterval <= TimeSpan.Zero)
            failures.Add($"{nameof(CollectorOptions.CycleInterval)} must be > 0 (got {options.CycleInterval}).");
        if (options.ErrorDelay <= TimeSpan.Zero)
            failures.Add($"{nameof(CollectorOptions.ErrorDelay)} must be > 0 (got {options.ErrorDelay}).");
        if (options.RateLimitDelaySeconds < 0)
            failures.Add($"{nameof(CollectorOptions.RateLimitDelaySeconds)} must be ≥ 0.");
        if (options.MaxRetries < 0)
            failures.Add($"{nameof(CollectorOptions.MaxRetries)} must be ≥ 0.");
        if (options.BatchSize <= 0)
            failures.Add($"{nameof(CollectorOptions.BatchSize)} must be > 0.");
        if (options.MaxConcurrentRequests <= 0)
            failures.Add($"{nameof(CollectorOptions.MaxConcurrentRequests)} must be > 0.");
        if (options.MemberCollectionPageSize is <= 0 or > 32)
            failures.Add($"{nameof(CollectorOptions.MemberCollectionPageSize)} must be in [1, 32] (RSI API limit).");
        if (options.OrganizationPageSize is <= 0 or > 20)
            failures.Add($"{nameof(CollectorOptions.OrganizationPageSize)} must be in [1, 20] (RSI API limit).");
        if (options.EmptyPagesThreshold <= 0)
            failures.Add($"{nameof(CollectorOptions.EmptyPagesThreshold)} must be > 0.");
        if (options.DiscoveryMaxPages < 0)
            failures.Add($"{nameof(CollectorOptions.DiscoveryMaxPages)} must be ≥ 0.");
        if (options.MetadataRefreshIntervalHours <= 0)
            failures.Add($"{nameof(CollectorOptions.MetadataRefreshIntervalHours)} must be > 0.");
        if (options.MaxEnrichmentAttempts <= 0)
            failures.Add($"{nameof(CollectorOptions.MaxEnrichmentAttempts)} must be > 0.");
        if (options.DiscoverSortMethods == null || options.DiscoverSortMethods.Length == 0)
            failures.Add($"{nameof(CollectorOptions.DiscoverSortMethods)} cannot be empty.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
