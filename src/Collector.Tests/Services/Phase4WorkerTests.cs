using Collector.Data.Repositories;
using Collector.Options;
using Collector.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>
/// The worker shares Phase 4 between the queue (new and unidentified members, first)
/// and reading again the profiles an older parser stored, at a capped pace.
/// </summary>
public sealed class Phase4WorkerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-27T12:00:00Z"));
    private readonly Mock<IUserEnrichmentQueueRepository> _queue = new();
    private readonly Mock<IUserCollector> _collector = new();
    private readonly List<long> _refreshedAfter = [];
    private readonly Queue<ProfileRefreshResult> _refreshResults = new();

    private Phase4Worker Worker(int profileRefreshPerHour = 3600)
    {
        _collector.Setup(c => c.RefreshProfilesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback((long afterId, CancellationToken _) => _refreshedAfter.Add(afterId))
            .ReturnsAsync(() => _refreshResults.TryDequeue(out var r) ? r : default);
        _collector.Setup(c => c.EnrichBatchAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnrichBatchResult(10, 0, 0, 0));
        var services = new ServiceCollection()
            .AddSingleton(_queue.Object)
            .AddSingleton(_collector.Object)
            .BuildServiceProvider();
        return new Phase4Worker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<Phase4Worker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new CollectorOptions { ProfileRefreshPerHour = profileRefreshPerHour }),
            _time);
    }

    private void QueuePending(int count)
        => _queue.Setup(q => q.CountPendingAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(count);

    private static ProfileRefreshResult Refreshed(long lastId, int count = 10) => new(lastId, count, 0, 0, 0, 0);

    [Fact]
    public async Task WhileTheQueueIsBusy_ItDrainsTheQueue_AndReadsNoProfileAgain()
    {
        QueuePending(500);
        var worker = Worker();

        (await worker.RunOnceAsync(default)).Should().Be(TimeSpan.Zero);

        _collector.Verify(c => c.EnrichBatchAsync(It.IsAny<CancellationToken>()), Times.Once);
        _refreshedAfter.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheQueueIsQuiet_ItReadsProfilesAgain_AtTheConfiguredPace()
    {
        QueuePending(0);
        _refreshResults.Enqueue(Refreshed(lastId: 42));
        var worker = Worker(profileRefreshPerHour: 3600);

        (await worker.RunOnceAsync(default)).Should().Be(TimeSpan.Zero);
        // 10 profiles at 3 600 an hour: the next batch 10 s after this one started.
        (await worker.RunOnceAsync(default)).Should().Be(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(10));
        await worker.RunOnceAsync(default);

        _refreshedAfter.Should().Equal(0, 42);
        _collector.Verify(c => c.EnrichBatchAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AtTheEndOfAPass_ItStartsOverFromTheFirstCitizen_ToRetryTheFailures()
    {
        QueuePending(0);
        _refreshResults.Enqueue(new ProfileRefreshResult(42, 9, 0, 0, 0, Failed: 1));
        _refreshResults.Enqueue(default); // nothing left after 42
        var worker = Worker();

        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromSeconds(10));
        await worker.RunOnceAsync(default);
        await worker.RunOnceAsync(default);

        _refreshedAfter.Should().Equal(0, 42, 0);
    }

    [Fact]
    public async Task APassWithoutFailures_IsTheLast_UntilSixHoursLater()
    {
        QueuePending(0);
        _refreshResults.Enqueue(Refreshed(lastId: 42));
        _refreshResults.Enqueue(default);
        var worker = Worker();

        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromSeconds(10));
        await worker.RunOnceAsync(default); // nothing after 42, and nothing failed
        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromHours(6));
        await worker.RunOnceAsync(default);

        _refreshedAfter.Should().Equal(0, 42, 0);
    }

    [Fact]
    public async Task WhenNoProfileIsLeft_ItLooksAgainSixHoursLater()
    {
        QueuePending(0);
        var worker = Worker();

        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromHours(5));
        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromHours(1));
        await worker.RunOnceAsync(default);

        _refreshedAfter.Should().Equal(0, 0);
    }

    [Fact]
    public async Task APassWithOnlyFailures_IsNotStartedOverStraightAway()
    {
        // RSI down or a page nobody can parse: going round again would only repeat the failures.
        QueuePending(0);
        _refreshResults.Enqueue(new ProfileRefreshResult(7, 0, 0, 0, 0, Failed: 1));
        _refreshResults.Enqueue(default);
        var worker = Worker();

        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromSeconds(1));
        await worker.RunOnceAsync(default);
        _time.Advance(TimeSpan.FromHours(1));
        await worker.RunOnceAsync(default);

        _refreshedAfter.Should().Equal(0, 7);
    }

    [Fact]
    public async Task ARateOfZero_TurnsTheRefreshOff()
    {
        QueuePending(0);
        var worker = Worker(profileRefreshPerHour: 0);

        (await worker.RunOnceAsync(default)).Should().Be(new CollectorOptions().Phase4IdleInterval);
        _refreshedAfter.Should().BeEmpty();
    }
}
