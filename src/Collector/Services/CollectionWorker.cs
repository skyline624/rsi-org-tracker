using Microsoft.Extensions.Hosting;

namespace Collector.Services;

/// <summary>
/// Runs the Phase 1-3 cycle loop as a hosted service, so the host's stop signal
/// (SIGTERM from systemd, Ctrl+C) cancels the cycle in progress.
/// </summary>
public sealed class CollectionWorker : BackgroundService
{
    private readonly CollectionOrchestrator _orchestrator;
    private readonly bool _skipPhase2;

    public CollectionWorker(CollectionOrchestrator orchestrator, bool skipPhase2)
    {
        _orchestrator = orchestrator;
        _skipPhase2 = skipPhase2;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _orchestrator.RunCollectionLoopAsync(stoppingToken, _skipPhase2);
}
