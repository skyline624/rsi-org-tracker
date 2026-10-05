using System.Collections.Concurrent;
using System.Threading.Channels;
using Collector.Api.Options;
using Collector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collector.Api.Services.Discord;

/// <summary>Coalesces sync/config notifications while the background linker is busy.</summary>
public sealed class DiscordAutoLinkQueue
{
    private readonly Channel<string> _guilds = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    public void Schedule(string guildId)
    {
        if (_pending.TryAdd(guildId, 0)) _guilds.Writer.TryWrite(guildId);
    }

    public async Task<string> ReadAsync(CancellationToken ct)
    {
        var guildId = await _guilds.Reader.ReadAsync(ct);
        _pending.TryRemove(guildId, out _);
        return guildId;
    }
}

/// <summary>
/// Validates strong links at startup and after a Discord sync or an org mapping. A periodic
/// pass also picks up RSI roster changes and interrupted work, without extending ingest requests.
/// Each guild gets its own scope and write-gate lease; shutdown cancels pending reads/writes.
/// </summary>
public sealed class DiscordAutoLinkService(
    IServiceScopeFactory scopes,
    DiscordAutoLinkQueue queue,
    IOptions<DiscordOptions> options,
    TimeProvider time,
    ILogger<DiscordAutoLinkService> logger) : BackgroundService
{
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoLink.Enabled) return;
        try
        {
            await SweepAsync(stoppingToken);
            var nextSweep = time.GetUtcNow() + SweepInterval;
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = nextSweep - time.GetUtcNow();
                if (wait <= TimeSpan.Zero)
                {
                    await SweepAsync(stoppingToken);
                    nextSweep = time.GetUtcNow() + SweepInterval;
                    continue;
                }

                using var timeout = new CancellationTokenSource(wait, time);
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
                try
                {
                    var guildId = await queue.ReadAsync(wake.Token);
                    await LinkGuildAsync(guildId, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            // Every guild: a member's own corpo tag makes strong links on unmapped guilds too.
            var guildIds = await db.DiscordGuilds.AsNoTracking()
                .OrderBy(g => g.GuildId).Select(g => g.GuildId).ToListAsync(ct);
            foreach (var guildId in guildIds) await LinkGuildAsync(guildId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Discord automatic linking sweep failed; retrying in {Interval}", SweepInterval);
        }
    }

    private async Task LinkGuildAsync(string guildId, CancellationToken ct)
    {
        try
        {
            var total = 0;
            int count;
            do
            {
                await using var scope = scopes.CreateAsyncScope();
                count = await scope.ServiceProvider.GetRequiredService<DiscordSuggestionService>()
                    .AutoLinkStrongAsync(guildId, ct);
                total += count;
            }
            while (count >= DiscordSuggestionService.AutomaticBatchSize);
            if (total > 0) logger.LogInformation(
                "Discord automatic linking: created {Count} strong links for guild {GuildId}", total, guildId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Discord automatic linking failed for guild {GuildId}; the next sweep will retry", guildId);
        }
    }
}
