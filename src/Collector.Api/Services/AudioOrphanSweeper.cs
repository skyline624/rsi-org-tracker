using Collector.Data.Repositories;

namespace Collector.Api.Services;

/// <summary>
/// Once a day, deletes recording files that no database row references (an upload whose
/// row was never written, or a row deleted while its file could not be removed).
/// </summary>
public sealed class AudioOrphanSweeper(
    IServiceScopeFactory scopes, AudioStorageService storage, ILogger<AudioOrphanSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>A file is written just before its row: leave recent files alone.</summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    /// <summary>Deletes files under <paramref name="baseDir"/> missing from <paramref name="knownPaths"/>
    /// and last written before <paramref name="olderThanUtc"/>. Returns how many were deleted.</summary>
    public static int Sweep(string baseDir, IReadOnlySet<string> knownPaths, DateTime olderThanUtc)
    {
        if (!Directory.Exists(baseDir)) return 0;

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (knownPaths.Contains(relative) || File.GetLastWriteTimeUtc(file) > olderThanUtc) continue;
            File.Delete(file);
            deleted++;
        }
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var known = await scope.ServiceProvider.GetRequiredService<IEntityAudioRepository>()
                    .GetAllStoredPathsAsync(stoppingToken);
                var deleted = Sweep(storage.BaseDirectory, known, DateTime.UtcNow - MinimumAge);
                if (deleted > 0) logger.LogInformation("Deleted {Count} orphaned audio files", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audio orphan sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
