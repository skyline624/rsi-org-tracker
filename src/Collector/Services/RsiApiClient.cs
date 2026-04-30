using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Collector.Dtos;
using Collector.Exceptions;
using Collector.Options;
using Collector.Parsers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Collector.Services;

/// <summary>
/// Client for the RSI API with resilience and rate limiting.
/// </summary>
public interface IRsiApiClient
{
    /// <summary>
    /// Gets a page of organizations from the RSI API.
    /// </summary>
    Task<IReadOnlyList<OrganizationData>?> GetOrganizationsAsync(
        int page = 1,
        string search = "",
        string sort = "",
        int pageSize = 12,
        CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for a single organization by SID.
    /// </summary>
    Task<OrganizationData?> GetOrganizationAsync(
        string sid,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all organizations using pagination.
    /// </summary>
    Task<IReadOnlyList<OrganizationData>> GetAllOrganizationsAsync(
        string[] sortMethods,
        int pageSize = 12,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a page of members for an organization.
    /// </summary>
    Task<(IReadOnlyList<MemberData> Members, int TotalRows)?> GetOrganizationMembersAsync(
        string orgSymbol,
        int page = 1,
        int pageSize = 32,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all members for an organization using pagination.
    /// Returns <see cref="MemberCollectionResult.OrgExists"/> = false when RSI
    /// reports the org as invalid (deleted/renamed), so the caller can flush
    /// stale active rows instead of leaving ghost members around.
    /// </summary>
    Task<MemberCollectionResult> GetAllOrganizationMembersAsync(
        string orgSymbol,
        int pageSize = 32,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the full HTML of an organization page (for description, history, manifesto, charter).
    /// Returns an <see cref="OrgPageFetchResult"/> so the caller can distinguish
    /// a real 404 (org gone from RSI — eligible for tombstoning) from a transient
    /// fetch failure (rate limit exhausted, network error — should keep retrying).
    /// </summary>
    Task<OrgPageFetchResult> GetOrgPageHtmlAsync(
        string sid,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a user's profile page HTML.
    /// </summary>
    Task<string?> GetUserProfileHtmlAsync(
        string handle,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a full member-collection pass for an organization.
///
/// - <see cref="OrgExists"/> = false means RSI responded with
///   <c>ErrInvalidOrganization</c> on the first page. The caller must treat
///   the current active roster as stale and deactivate it.
/// - <see cref="Reachable"/> = false means we couldn't tell (network error,
///   null response). The caller should keep the previous roster as-is.
/// </summary>
public record MemberCollectionResult(
    IReadOnlyList<MemberData> Members,
    bool OrgExists,
    bool Reachable);

/// <summary>
/// Outcome of a single org-page HTML fetch.
/// </summary>
public enum OrgPageFetchOutcome
{
    /// <summary>HTTP 200, body returned.</summary>
    Ok,
    /// <summary>RSI returned 404 — org has been deleted/privatized.</summary>
    NotFound,
    /// <summary>Transient failure (network, 5xx, retries exhausted, parse error).</summary>
    Failed,
}

public record OrgPageFetchResult(string? Html, OrgPageFetchOutcome Outcome);

public class RsiApiClient : IRsiApiClient, IDisposable
{
    private const string BaseUrl = "https://robertsspaceindustries.com";
    private const string ApiPath = "/api";

    private readonly HttpClient _httpClient;
    private readonly ILogger<RsiApiClient> _logger;
    private readonly CollectorOptions _options;
    private readonly OrganizationHtmlParser _orgParser;
    private readonly MemberHtmlParser _memberParser;
    private readonly IAsyncPolicy<HttpResponseMessage> _resiliencePolicy;
    private readonly SemaphoreSlim _rateLimitSemaphore;
    private readonly SemaphoreSlim _concurrentPageSemaphore;
    private DateTime _lastRequestTime = DateTime.MinValue;

    public RsiApiClient(
        HttpClient httpClient,
        ILogger<RsiApiClient> logger,
        IOptions<CollectorOptions> options,
        OrganizationHtmlParser orgParser,
        MemberHtmlParser memberParser)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _orgParser = orgParser;
        _memberParser = memberParser;
        _rateLimitSemaphore = new SemaphoreSlim(1, 1);
        _concurrentPageSemaphore = new SemaphoreSlim(
            _options.MaxConcurrentRequests,
            _options.MaxConcurrentRequests);

        // Retry only on genuine transient failures. Throttling (HTTP 429 / 503 /
        // ErrApiThrottled) is handled above in PostAsync and in the page-fetch helpers
        // so it doesn't compound exponentially with this policy.
        var retryPolicy = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r =>
                (int)r.StatusCode >= 500
                && r.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
            .WaitAndRetryAsync(
                retryCount: _options.MaxRetries,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Retry attempt {RetryCount} after {Delay}ms. Reason: {Reason}",
                        retryCount,
                        timeSpan.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString());
                });

        // Circuit breaker on genuine 5xx + network errors only. Throttles must not open
        // the breaker (they are normal back-pressure, not an outage).
        var circuitBreakerPolicy = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r =>
                (int)r.StatusCode >= 500
                && r.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
            .AdvancedCircuitBreakerAsync(
                failureThreshold: 0.5,
                samplingDuration: TimeSpan.FromSeconds(30),
                minimumThroughput: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (exception, duration) =>
                {
                    _logger.LogWarning(
                        "Circuit breaker opened for {Duration}s due to: {Reason}",
                        duration.TotalSeconds,
                        exception.Exception?.Message ?? exception.Result?.StatusCode.ToString());
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit breaker reset");
                });

        // Combine policies: retry first, then circuit breaker
        _resiliencePolicy = Policy.WrapAsync(circuitBreakerPolicy, retryPolicy);
    }

    private async Task ApplyRateLimitAsync(CancellationToken ct)
    {
        await _rateLimitSemaphore.WaitAsync(ct);
        try
        {
            var delay = _options.RateLimitDelaySeconds;
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed.TotalSeconds < delay)
            {
                var wait = TimeSpan.FromSeconds(delay) - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }
            }
            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    private async Task<JsonNode?> PostAsync(string endpoint, object payload, CancellationToken ct)
    {
        const int maxThrottleRetries = 5;
        var throttleDelay = TimeSpan.FromSeconds(5);

        var url = $"{BaseUrl}{ApiPath}/{endpoint}";
        var json = JsonSerializer.Serialize(payload);

        for (int throttleAttempt = 0; throttleAttempt < maxThrottleRetries; throttleAttempt++)
        {
            await ApplyRateLimitAsync(ct);

            // StringContent is not reusable across retries — rebuild per attempt.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _resiliencePolicy.ExecuteAsync(
                async token => await _httpClient.PostAsync(url, content, token),
                ct);

            // HTTP-level throttling (429) — honour Retry-After when present, otherwise use
            // our exponential backoff. This MUST be checked before EnsureSuccessStatusCode
            // so throttle never masquerades as a generic failure.
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta
                    ?? throttleDelay;
                _logger.LogWarning(
                    "RSI API HTTP {Status} for {Endpoint}. Waiting {Delay}s before retry {Attempt}/{Max}",
                    (int)response.StatusCode, endpoint, wait.TotalSeconds,
                    throttleAttempt + 1, maxThrottleRetries);
                await Task.Delay(wait, ct);
                throttleDelay = TimeSpan.FromSeconds(Math.Min(throttleDelay.TotalSeconds * 2, 300));
                continue;
            }

            // Any other non-success status is considered a hard failure (Polly already
            // retried transient 5xx before we got here).
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct);
            var data = JsonNode.Parse(responseText);

            // Application-level throttling: RSI returns HTTP 200 with
            // `{ "code": "ErrApiThrottled" }`. Same backoff as HTTP 429.
            if (data?["code"]?.GetValue<string>() == "ErrApiThrottled")
            {
                _logger.LogWarning(
                    "RSI API application-level throttle for {Endpoint}. Waiting {Delay}s before retry {Attempt}/{Max}",
                    endpoint, throttleDelay.TotalSeconds,
                    throttleAttempt + 1, maxThrottleRetries);
                await Task.Delay(throttleDelay, ct);
                throttleDelay = TimeSpan.FromSeconds(Math.Min(throttleDelay.TotalSeconds * 2, 300));
                continue;
            }

            return data;
        }

        _logger.LogError("Max throttle retries exceeded for {Endpoint}", endpoint);
        throw new RsiThrottledException();
    }

    public async Task<OrganizationData?> GetOrganizationAsync(
        string sid,
        CancellationToken ct = default)
    {
        var orgs = await GetOrganizationsAsync(page: 1, search: sid, pageSize: 1, ct: ct);
        return orgs?.FirstOrDefault(o => string.Equals(o.Sid, sid, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<OrganizationData>?> GetOrganizationsAsync(
        int page = 1,
        string search = "",
        string sort = "",
        int pageSize = 12,
        CancellationToken ct = default)
    {
        var payload = new
        {
            sort,
            search,
            commitment = Array.Empty<object>(),
            roleplay = Array.Empty<object>(),
            size = Array.Empty<object>(),
            model = Array.Empty<object>(),
            activity = Array.Empty<object>(),
            language = Array.Empty<object>(),
            recruiting = Array.Empty<object>(),
            pagesize = pageSize,
            page
        };

        var data = await PostAsync("orgs/getOrgs", payload, ct);
        if (data == null)
        {
            return null;
        }

        var html = data["data"]?["html"]?.GetValue<string>();
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var organizations = _orgParser.ParseOrganizations(html);
        return organizations;
    }

    public async Task<IReadOnlyList<OrganizationData>> GetAllOrganizationsAsync(
        string[] sortMethods,
        int pageSize = 12,
        CancellationToken ct = default)
    {
        var allOrganizations = new Dictionary<string, OrganizationData>();

        foreach (var sortMethod in sortMethods)
        {
            _logger.LogInformation("Discovering organizations with sort: {SortMethod}", sortMethod);

            var page = 1;
            var emptyPages = 0;

            while (emptyPages < _options.EmptyPagesThreshold)
            {
                var orgs = await GetOrganizationsAsync(page, "", sortMethod, pageSize, ct);
                if (orgs == null || orgs.Count == 0)
                {
                    emptyPages++;
                    page++;
                    continue;
                }

                emptyPages = 0;
                foreach (var org in orgs)
                {
                    allOrganizations[org.Sid] = org;
                }

                if (page % 10 == 0)
                {
                    _logger.LogInformation(
                        "Discovery progress: page {Page}, {Count} organizations found so far",
                        page, allOrganizations.Count);
                }

                page++;
            }
        }

        return allOrganizations.Values.ToList();
    }

    public async Task<(IReadOnlyList<MemberData> Members, int TotalRows)?> GetOrganizationMembersAsync(
        string orgSymbol,
        int page = 1,
        int pageSize = 32,
        CancellationToken ct = default)
    {
        var payload = new
        {
            symbol = orgSymbol,
            search = "",
            pagesize = pageSize,
            page
        };

        var data = await PostAsync("orgs/getOrgMembers", payload, ct);
        if (data == null)
        {
            return null;
        }

        // Check for invalid organization
        if (data["code"]?.GetValue<string>() == "ErrInvalidOrganization")
        {
            _logger.LogWarning("Invalid organization: {OrgSymbol}", orgSymbol);
            return (Array.Empty<MemberData>(), 0);
        }

        var html = data["data"]?["html"]?.GetValue<string>();
        var totalRows = data["data"]?["totalrows"]?.GetValue<int>() ?? 0;

        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var members = _memberParser.ParseMembers(html, orgSymbol);
        return (members, totalRows);
    }

    public async Task<MemberCollectionResult> GetAllOrganizationMembersAsync(
        string orgSymbol,
        int pageSize = 32,
        CancellationToken ct = default)
    {
        var allMembers = new List<MemberData>();
        var page = 1;
        int totalRows;
        var firstPage = true;
        var reachable = false;

        do
        {
            var result = await GetOrganizationMembersAsync(orgSymbol, page, pageSize, ct);
            if (result == null)
            {
                // Null on first page = couldn't reach / parse. We don't know whether
                // the org still exists, so we signal "unreachable" and let the caller
                // keep the existing roster untouched.
                if (firstPage)
                {
                    return new MemberCollectionResult(
                        Array.Empty<MemberData>(),
                        OrgExists: true, // unknown, assume it exists
                        Reachable: false);
                }
                break;
            }

            reachable = true;
            var (members, total) = result.Value;
            totalRows = total;

            // Empty response on first page is RSI's signal for "this org doesn't
            // exist anymore" (totalrows = 0, ErrInvalidOrganization already
            // mapped to empty in GetOrganizationMembersAsync). For the roster
            // cleanup to be safe we require first-page-confirmed empty.
            if (members.Count == 0)
            {
                if (firstPage)
                {
                    return new MemberCollectionResult(
                        Array.Empty<MemberData>(),
                        OrgExists: totalRows > 0, // org exists but genuinely 0 members is rare; usually this means deleted
                        Reachable: true);
                }
                break;
            }

            firstPage = false;
            allMembers.AddRange(members);

            // Check for completeness (> 95%)
            if (page == 1)
            {
                _logger.LogInformation(
                    "Organization {OrgSymbol}: {Count}/{Total} members collected",
                    orgSymbol, members.Count, totalRows);
            }

            page++;
        }
        while (allMembers.Count < totalRows);

        return new MemberCollectionResult(allMembers, OrgExists: true, Reachable: reachable);
    }

    public async Task<OrgPageFetchResult> GetOrgPageHtmlAsync(string sid, CancellationToken ct = default)
    {
        await _concurrentPageSemaphore.WaitAsync(ct);
        try
        {
            var url = $"{BaseUrl}/en/orgs/{sid}";
            const int maxRetries = 4;
            var backoff = TimeSpan.FromSeconds(30);

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                // Same global throttle as the API endpoints — without it we burst
                // 5 concurrent requests with no inter-request delay, which trips
                // Cloudflare on robertsspaceindustries.com.
                await ApplyRateLimitAsync(ct);

                var response = await _resiliencePolicy.ExecuteAsync(async token =>
                    await _httpClient.GetAsync(url, token), ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Org page not found: {Sid}", sid);
                    return new OrgPageFetchResult(null, OrgPageFetchOutcome.NotFound);
                }

                // Rate limited / Cloudflare push-back — wait and retry with exponential backoff.
                // 403 is treated like a throttle: retrying after a long enough pause often recovers.
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning(
                        "Throttled fetching org page {Sid} (HTTP {Code}). Waiting {Delay}s (attempt {Attempt}/{Max})",
                        sid, (int)response.StatusCode, backoff.TotalSeconds, attempt + 1, maxRetries);
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2); // 30s → 60s → 120s → 240s
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(ct);
                return new OrgPageFetchResult(html, OrgPageFetchOutcome.Ok);
            }

            _logger.LogError("Max retries exceeded for org page {Sid}", sid);
            return new OrgPageFetchResult(null, OrgPageFetchOutcome.Failed);
        }
        finally
        {
            _concurrentPageSemaphore.Release();
        }
    }

    public async Task<string?> GetUserProfileHtmlAsync(string handle, CancellationToken ct = default)
    {
        await _concurrentPageSemaphore.WaitAsync(ct);
        try
        {
            // Hit /en/citizens directly — without the prefix RSI returns 301
            // and we waste a redirect hop on every fetch.
            var url = $"{BaseUrl}/en/citizens/{handle}";
            const int maxRetries = 4;
            var backoff = TimeSpan.FromSeconds(30);

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                // Same global throttle as API endpoints — required to avoid
                // Cloudflare 403'ing the IP on burst requests.
                await ApplyRateLimitAsync(ct);

                var response = await _resiliencePolicy.ExecuteAsync(async token =>
                    await _httpClient.GetAsync(url, token), ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("User profile not found: {Handle}", handle);
                    return null;
                }

                // Cloudflare push-back (403) is handled like 429/503: pause and retry.
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning(
                        "Throttled fetching profile {Handle} (HTTP {Code}). Waiting {Delay}s (attempt {Attempt}/{Max})",
                        handle, (int)response.StatusCode, backoff.TotalSeconds, attempt + 1, maxRetries);
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }

            _logger.LogError("Max retries exceeded for user profile {Handle}", handle);
            return null;
        }
        finally
        {
            _concurrentPageSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _rateLimitSemaphore.Dispose();
        _concurrentPageSemaphore.Dispose();
    }
}