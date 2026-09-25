using System.Collections.Concurrent;
using System.Diagnostics;
using Collector.Extensions;
using Collector.Options;
using Collector.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Collector.Tests.Services;

public sealed class CollectionOrchestratorTests
{
    /// <summary>Scoped marker: tells which DI scope a phase ran in, and whether it was disposed.</summary>
    private sealed class ScopeProbe : IDisposable
    {
        private static int _next;
        public int Id { get; } = Interlocked.Increment(ref _next);
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed record PhaseRun(string Phase, ScopeProbe Scope);

    private static ServiceCollection ServicesWithFakePhases(
        ConcurrentQueue<PhaseRun> runs,
        Func<CancellationToken, Task>? phase1 = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<CollectorOptions>();
        services.AddScoped<ScopeProbe>();
        services.AddScoped(sp =>
        {
            var scope = sp.GetRequiredService<ScopeProbe>();
            var org = new Mock<IOrganizationCollector>();
            org.Setup(o => o.DiscoverOrganizationsAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken ct) =>
                {
                    runs.Enqueue(new PhaseRun("1", scope));
                    if (phase1 != null) await phase1(ct);
                    return 0;
                });
            org.Setup(o => o.CollectOrganizationMetadataAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { runs.Enqueue(new PhaseRun("2", scope)); return 0; });
            return org.Object;
        });
        services.AddScoped(sp =>
        {
            var scope = sp.GetRequiredService<ScopeProbe>();
            var members = new Mock<IMemberCollector>();
            members.Setup(m => m.CollectAllMembersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { runs.Enqueue(new PhaseRun("3", scope)); return 0; });
            return members.Object;
        });
        services.AddSingleton<CollectionOrchestrator>();
        return services;
    }

    [Fact]
    public async Task EachPhase_RunsInItsOwnScope_DisposedWhenThePhaseEnds()
    {
        var runs = new ConcurrentQueue<PhaseRun>();
        await using var provider = ServicesWithFakePhases(runs)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await provider.GetRequiredService<CollectionOrchestrator>().RunSingleCycleAsync();

        runs.Select(r => r.Phase).Should().Equal("1", "2", "3");
        runs.Select(r => r.Scope.Id).Should().OnlyHaveUniqueItems("a DbContext shared across phases keeps every row it ever loaded");
        runs.Should().OnlyContain(r => r.Scope.Disposed);
    }

    [Fact]
    public void CollectorRegistrations_PassScopeAndBuildValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collector:DiscoverSortMethods:0"] = "active" })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddCollectorServices(config, Path.GetTempPath(), registerHostedServices: true);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetRequiredService<CollectionOrchestrator>().Should().NotBeNull();
        provider.GetServices<IHostedService>().Should().Contain(s => s is CollectionWorker);
    }

    [Fact]
    public async Task Stopping_TheHost_CancelsTheRunningCycle_InUnderFiveSeconds()
    {
        var runs = new ConcurrentQueue<PhaseRun>();
        var phaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = ServicesWithFakePhases(runs, async ct =>
        {
            phaseStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        services.AddHostedService(sp => new CollectionWorker(
            sp.GetRequiredService<CollectionOrchestrator>(), skipPhase2: false));

        using var host = new HostBuilder()
            .ConfigureServices(s => { foreach (var d in services) s.Add(d); })
            .Build();
        await host.StartAsync();
        await phaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(TimeSpan.FromSeconds(30));

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        host.Services.GetServices<IHostedService>().OfType<CollectionWorker>().Single()
            .ExecuteTask!.IsCompleted.Should().BeTrue();
    }
}
