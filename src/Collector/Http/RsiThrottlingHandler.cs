using System.Net;
using Collector.Options;
using Microsoft.Extensions.Options;

namespace Collector.Http;

/// <summary>
/// Sends every RSI request through the shared <see cref="RsiRateGate"/> and reports
/// throttles back to it. Also enforces the per-request timeout: HttpClient.Timeout
/// would count the time spent queued in the gate, so the client has none and the
/// timeout starts here, once the request is allowed out.
/// </summary>
public sealed class RsiThrottlingHandler : DelegatingHandler
{
    private readonly RsiRateGate _gate;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public RsiThrottlingHandler(RsiRateGate gate, IOptions<CollectorOptions> options, TimeProvider time)
    {
        _gate = gate;
        _time = time;
        _timeout = TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var lease = await _gate.AcquireAsync(ct);
        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, linked.Token);
            // Read the body inside the timeout too, and before the slot is released.
            await response.Content.LoadIntoBufferAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            response?.Dispose();
            throw new TaskCanceledException(
                $"RSI request timed out after {_timeout.TotalSeconds}s: {request.RequestUri}",
                new TimeoutException());
        }
        catch
        {
            response?.Dispose();
            throw;
        }

        // Success is reported by RsiApiClient once it has read the body: RSI's
        // ErrApiThrottled comes with HTTP 200, and must not reset the pause escalation.
        if (IsThrottle(response.StatusCode))
            _gate.ReportThrottled(RetryAfter(response));

        return response;
    }

    /// <summary>Cloudflare answers 403 when it blocks the IP for a while: treated like 429/503.</summary>
    private static bool IsThrottle(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Forbidden;

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - _time.GetUtcNow();
        return null;
    }
}
