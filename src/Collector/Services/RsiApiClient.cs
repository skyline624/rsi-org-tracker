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
    /// </summary>
    Task<IReadOnlyList<MemberData>> GetAllOrganizationMembersAsync(
        string orgSymbol,
        int pageSize = 32,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a user's profile page HTML.
    /// </summary>
    Task<string?> GetUserProfileHtmlAsync(
        string handle,
        CancellationToken ct = default);
}

public class RsiApiClient : IRsiApiClient
{
    private const string BaseUrl = "https://robertsspaceindustries.com";
    private const string ApiPath = "/api";

    private readonly HttpClient _httpClient;
    private readonly ILogger<RsiApiClient> _logger;
    private readonly CollectorOptions _options;
    private readonly OrganizationHtmlParser _orgParser;
    private readonly MemberHtmlParser _memberParser;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private readonly SemaphoreSlim _rateLimitSemaphore;
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

        // Configure retry policy with exponential backoff
        _retryPolicy = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
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

        for (int throttleAttempt = 0; throttleAttempt < maxThrottleRetries; throttleAttempt++)
        {
            await ApplyRateLimitAsync(ct);

            var url = $"{BaseUrl}{ApiPath}/{endpoint}";
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _retryPolicy.ExecuteAsync(async token =>
            {
                return await _httpClient.PostAsync(url, content, token);
            }, ct);

            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct);
            var data = JsonNode.Parse(responseText);

            // Check for throttling - retry with exponential backoff
            if (data?["code"]?.GetValue<string>() == "ErrApiThrottled")
            {
                _logger.LogWarning(
                    "RSI API throttled. Waiting {Delay}s before retry {Attempt}/{MaxRetries}",
                    throttleDelay.TotalSeconds,
                    throttleAttempt + 1,
                    maxThrottleRetries);

                await Task.Delay(throttleDelay, ct);

                // Exponential backoff: 5s, 10s, 20s, 40s, 80s
                throttleDelay = TimeSpan.FromSeconds(throttleDelay.TotalSeconds * 2);
                continue;
            }

            return data;
        }

        _logger.LogError("Max throttle retries exceeded for {Endpoint}", endpoint);
        throw new RsiThrottledException();
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
                    continue;
                }

                emptyPages = 0;
                foreach (var org in orgs)
                {
                    allOrganizations[org.Sid] = org;
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

    public async Task<IReadOnlyList<MemberData>> GetAllOrganizationMembersAsync(
        string orgSymbol,
        int pageSize = 32,
        CancellationToken ct = default)
    {
        var allMembers = new List<MemberData>();
        var page = 1;
        int totalRows;

        do
        {
            var result = await GetOrganizationMembersAsync(orgSymbol, page, pageSize, ct);
            if (result == null)
            {
                break;
            }

            var (members, total) = result.Value;
            totalRows = total;

            if (members.Count == 0)
            {
                break;
            }

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

        return allMembers;
    }

    public async Task<string?> GetUserProfileHtmlAsync(string handle, CancellationToken ct = default)
    {
        await ApplyRateLimitAsync(ct);

        var url = $"{BaseUrl}/citizens/{handle}";
        var response = await _retryPolicy.ExecuteAsync(async token =>
        {
            return await _httpClient.GetAsync(url, token);
        }, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("User profile not found: {Handle}", handle);
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}