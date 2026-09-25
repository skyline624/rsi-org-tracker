using Collector.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

/// <summary>
/// Deletes refresh tokens that can no longer be used, once a day. Revoked tokens are kept
/// for a week so reuse detection still recognises a stolen token replayed shortly after.
/// </summary>
public class RefreshTokenCleanupService(IServiceScopeFactory scopes, ILogger<RefreshTokenCleanupService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static Task<int> PurgeAsync(ApiDbContext db, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - Retention;
        return db.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
                var deleted = await PurgeAsync(db, DateTime.UtcNow, stoppingToken);
                if (deleted > 0) logger.LogInformation("Purged {Count} unusable refresh tokens", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Refresh token cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
