using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Collector.Dtos;
using Collector.Exceptions;
using Collector.Http;
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
    /// Gets a user's profile page, distinguishing a 404 (handle gone) from a
    /// transient failure so the enrichment worker can react differently.
    /// </summary>
    Task<UserProfileFetchResult> GetUserProfileResultAsync(
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

/// <summary>
/// Outcome of a single user-profile HTML fetch. Lets the enrichment worker tell a
/// permanent 404 (handle gone from RSI — stop retrying) apart from a transient
/// failure (throttle/network — keep retrying).
/// </summary>
public enum UserProfileFetchOutcome
{
    /// <summary>HTTP 200, body returned.</summary>
    Ok,
    /// <summary>RSI returned 404 — handle deleted or renamed.</summary>
    NotFound,
    /// <summary>Transient failure (Cloudflare 403/429/503, network, retries exhausted).</summary>
    Failed,
}

public record UserProfileFetchResult(string? Html, UserProfileFetchOutcome Outcome);

/// <summary>
/// RSI endpoints. Pacing, concurrency, shared throttle pauses and the per-request
/// timeout live in <see cref="RsiRateGate"/> / <see cref="RsiThrottlingHandler"/>,
/// shared by every instance; this class only retries.
/// </summary>
public class RsiApiClient : IRsiApiClient
{
    private const string BaseUrl = "https://robertsspaceindustries.com";
    private const string ApiPath = "/api";

    private readonly HttpClient _httpClient;
    private readonly ILogger<RsiApiClient> _logger;
    private readonly CollectorOptions _options;
    private readonly OrganizationHtmlParser _orgParser;
    private readonly MemberHtmlParser _memberParser;
    private readonly IAsyncPolicy<HttpResponseMessage> _resiliencePolicy;
    private readonly RsiRateGate _gate;

    public RsiApiClient(
        HttpClient httpClient,
        ILogger<RsiApiClient> logger,
        IOptions<CollectorOptions> options,
        OrganizationHtmlParser orgParser,
        MemberHtmlParser memberParser,
        RsiRateGate gate)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _orgParser = orgParser;
        _memberParser = memberParser;
        _gate = gate;

        // Retry only on genuine transient failures. Throttling (HTTP 403 / 429 / 503 /
        // ErrApiThrottled) pauses the shared gate and is retried by PostAsync and the
        // page-fetch helpers, so it doesn't compound exponentially with this policy.
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

    private async Task<JsonNode?> PostAsync(string endpoint, object payload, CancellationToken ct)
    {
        const int maxThrottleRetries = 5;

        var url = $"{BaseUrl}{ApiPath}/{endpoint}";
        var json = JsonSerializer.Serialize(payload);

        for (int throttleAttempt = 0; throttleAttempt < maxThrottleRetries; throttleAttempt++)
        {
            // StringContent is not reusable across retries — rebuild per attempt.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _resiliencePolicy.ExecuteAsync(
                async token => await _httpClient.PostAsync(url, content, token),
                ct);

            // HTTP-level throttling (429/503): the handler has paused the shared gate
            // (honouring Retry-After), so the next attempt waits for it. This MUST be
            // checked before EnsureSuccessStatusCode so throttle never masquerades as
            // a generic failure.
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning(
                    "RSI API HTTP {Status} for {Endpoint}; retry {Attempt}/{Max} after the shared pause",
                    (int)response.StatusCode, endpoint, throttleAttempt + 1, maxThrottleRetries);
                continue;
            }

            // Any other non-success status is considered a hard failure (Polly already
            // retried transient 5xx before we got here).
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct);
            var data = JsonNode.Parse(responseText);

            // Application-level throttling: RSI returns HTTP 200 with
            // `{ "code": "ErrApiThrottled" }`. Same shared pause as HTTP 429.
            if (data?["code"]?.GetValue<string>() == "ErrApiThrottled")
            {
                _gate.ReportThrottled();
                _logger.LogWarning(
                    "RSI API application-level throttle for {Endpoint}; retry {Attempt}/{Max} after the shared pause",
                    endpoint, throttleAttempt + 1, maxThrottleRetries);
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
            _logger.LogWarning(
                "RSI getOrgMembers returned no roster HTML for {OrgSymbol} page {Page} (code={Code}, msg={Msg})",
                orgSymbol, page, data["code"]?.ToString(), data["msg"]?.ToString());
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
        var (html, status) = await GetPageAsync($"{BaseUrl}/en/orgs/{sid}", "org page", sid, ct);
        return status switch
        {
            System.Net.HttpStatusCode.OK => new OrgPageFetchResult(html, OrgPageFetchOutcome.Ok),
            System.Net.HttpStatusCode.NotFound => new OrgPageFetchResult(null, OrgPageFetchOutcome.NotFound),
            _ => new OrgPageFetchResult(null, OrgPageFetchOutcome.Failed),
        };
    }

    public async Task<UserProfileFetchResult> GetUserProfileResultAsync(string handle, CancellationToken ct = default)
    {
        // Hit /en/citizens directly — without the prefix RSI returns 301
        // and we waste a redirect hop on every fetch.
        var (html, status) = await GetPageAsync($"{BaseUrl}/en/citizens/{handle}", "profile", handle, ct);
        return status switch
        {
            System.Net.HttpStatusCode.OK => new UserProfileFetchResult(html, UserProfileFetchOutcome.Ok),
            System.Net.HttpStatusCode.NotFound => new UserProfileFetchResult(null, UserProfileFetchOutcome.NotFound),
            _ => new UserProfileFetchResult(null, UserProfileFetchOutcome.Failed),
        };
    }

    /// <summary>
    /// GETs an HTML page. Returns (html, OK), (null, NotFound), or (null, TooManyRequests)
    /// once the retries are exhausted. A throttle (403/429/503) has already paused the
    /// shared gate, so the next attempt simply waits for it.
    /// </summary>
    private async Task<(string? Html, System.Net.HttpStatusCode Status)> GetPageAsync(
        string url, string kind, string key, CancellationToken ct)
    {
        const int maxRetries = 4;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            using var response = await _resiliencePolicy.ExecuteAsync(async token =>
                await _httpClient.GetAsync(url, token), ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("RSI {Kind} not found: {Key}", kind, key);
                return (null, System.Net.HttpStatusCode.NotFound);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Throttled fetching {Kind} {Key} (HTTP {Code}); retry {Attempt}/{Max} after the shared pause",
                    kind, key, (int)response.StatusCode, attempt + 1, maxRetries);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(ct), System.Net.HttpStatusCode.OK);
        }

        _logger.LogError("Max retries exceeded for {Kind} {Key}", kind, key);
        return (null, System.Net.HttpStatusCode.TooManyRequests);
    }
}