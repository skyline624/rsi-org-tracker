using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Collector.Http;
using Collector.Options;
using Collector.Parsers;
using Collector.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Collector.Tests.Http;

/// <summary>
/// One request budget for the whole process: every RsiApiClient instance (one per
/// DI scope, plus the Phase 4 worker in parallel) goes through the same gate.
/// </summary>
public sealed class RsiRateGateTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
    private readonly StubRsi _rsi = new();

    private RsiRateGate Gate(double spacingSeconds = 1, int maxConcurrent = 5) => new(
        Microsoft.Extensions.Options.Options.Create(new CollectorOptions
        {
            RateLimitDelaySeconds = spacingSeconds,
            MaxConcurrentRequests = maxConcurrent,
            ThrottlePauseSeconds = 30,
            MaxThrottlePauseSeconds = 300,
        }),
        _time,
        NullLogger<RsiRateGate>.Instance);

    /// <summary>What each typed-client instance gets: its own handler, the shared gate.</summary>
    private HttpClient Client(RsiRateGate gate) => new(new RsiThrottlingHandler(
        gate,
        Microsoft.Extensions.Options.Options.Create(new CollectorOptions { RequestTimeoutSeconds = 30 }),
        _time)
    { InnerHandler = _rsi })
    { BaseAddress = new Uri("https://robertsspaceindustries.com/"), Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task TwoClientInstances_ShareOneSpacingBudget()
    {
        var gate = Gate(spacingSeconds: 1);
        var first = Client(gate);
        var second = Client(gate);

        await first.GetAsync("a");
        var pending = second.GetAsync("b");
        await Settle();

        _rsi.Calls.Should().Be(1, "the second instance must wait for the shared spacing");
        _time.Advance(TimeSpan.FromSeconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        _rsi.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A429_PausesEveryCaller_ForItsRetryAfter()
    {
        var gate = Gate(spacingSeconds: 0);
        _rsi.Responses.Enqueue(() => Throttled(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(90)));

        (await Client(gate).GetAsync("a")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var pending = Client(gate).GetAsync("b");

        _time.Advance(TimeSpan.FromSeconds(89));
        await Settle();
        _rsi.Calls.Should().Be(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        _rsi.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CloudflarePushBack_PausesEveryCaller(HttpStatusCode status)
    {
        var gate = Gate(spacingSeconds: 0);
        _rsi.Responses.Enqueue(() => Throttled(status));

        await Client(gate).GetAsync("a");
        var pending = Client(gate).GetAsync("b");

        _time.Advance(TimeSpan.FromSeconds(29));
        await Settle();
        _rsi.Calls.Should().Be(1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ConsecutiveThrottleEpisodes_DoubleThePause_UntilASuccess()
    {
        var gate = Gate();

        gate.ReportThrottled();
        gate.PausedFor.Should().Be(TimeSpan.FromSeconds(30));
        gate.ReportThrottled(); // same episode (e.g. concurrent requests all refused): no escalation
        gate.PausedFor.Should().Be(TimeSpan.FromSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(30));
        gate.ReportThrottled();
        gate.PausedFor.Should().Be(TimeSpan.FromSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(60));
        gate.ReportSuccess();
        gate.ReportThrottled();
        gate.PausedFor.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ConcurrentRequests_AreCappedProcessWide()
    {
        var gate = Gate(spacingSeconds: 0, maxConcurrent: 2);
        var release = new TaskCompletionSource();
        _rsi.Hold = release.Task;

        var requests = Enumerable.Range(0, 3).Select(i => Client(gate).GetAsync($"r{i}")).ToList();
        await Settle();
        _rsi.Calls.Should().Be(2);

        release.SetResult();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        _rsi.Calls.Should().Be(3);
    }

    [Fact]
    public async Task AHangingRequest_TimesOutAfter30Seconds_AsARetryableTimeout()
    {
        var gate = Gate(spacingSeconds: 0);
        _rsi.Hold = new TaskCompletionSource().Task; // never answers

        var pending = Client(gate).GetAsync("slow");
        await Settle();
        _time.Advance(TimeSpan.FromSeconds(30));

        var thrown = await pending.Invoking(p => p.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<TaskCanceledException>();
        thrown.WithInnerException<TimeoutException>();
    }

    [Fact]
    public async Task ErrApiThrottled_PausesTheSharedGate_ThenRetries()
    {
        var gate = Gate(spacingSeconds: 0);
        _rsi.Responses.Enqueue(() => Json("""{"success":0,"code":"ErrApiThrottled"}"""));
        _rsi.Responses.Enqueue(() => Json("""{"success":1,"code":"OK","data":{"totalrows":0,"html":""}}"""));
        var options = new CollectorOptions();
        var client = new RsiApiClient(Client(gate), NullLogger<RsiApiClient>.Instance,
            Microsoft.Extensions.Options.Options.Create(options),
            new OrganizationHtmlParser(NullLogger<OrganizationHtmlParser>.Instance),
            new MemberHtmlParser(NullLogger<MemberHtmlParser>.Instance),
            gate);

        var pending = client.GetAllOrganizationMembersAsync("SLOW");
        await Settle();
        _rsi.Calls.Should().Be(1);
        gate.PausedFor.Should().Be(TimeSpan.FromSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(30));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        _rsi.Calls.Should().Be(2);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Throttled(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    /// <summary>Lets continuations scheduled by the fake clock run.</summary>
    private static Task Settle() => Task.Delay(100);

    private sealed class StubRsi : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<Func<HttpResponseMessage>> Responses { get; } = new();
        public Task Hold { get; set; } = Task.CompletedTask;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Hold.WaitAsync(ct);
            return Responses.TryDequeue(out var next) ? next() : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
