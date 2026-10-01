using System.Globalization;
using Collector.Api.Options;
using Collector.Api.Services.Discord;
using Collector.Data;
using Collector.Extensions;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The retention pass runs after a startup delay and then once per interval, takes the
/// Discord write gate for every batch, and loops until a batch comes back short. It works on
/// its own tracker.db, so its purges never touch the shared test database; only the write
/// gate comes from the shared API host.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DiscordRetentionServiceTests(ApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Older than both retention thresholds (365 and 730 days).</summary>
    private static readonly DateTime LongAgo = Start.UtcDateTime.AddDays(-800);

    private const string GuildId = "300000000000000001";
    private static long _next;

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "sc-tracker-retention-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider _provider = null!;
    private readonly ProbeLogger _logger = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _provider = new ServiceCollection()
            .AddLogging()
            .AddCollectorDataServices(new ConfigurationBuilder().Build(), _dataDir)
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(_dataDir);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { /* SQLite handles may linger */ }
    }

    [Fact]
    public async Task APass_PurgesBatchAfterBatch_UntilABatchComesBackShort()
    {
        for (var i = 0; i < 5; i++) await SeedSyncAsync(LongAgo);
        await SeedSyncAsync(Start.UtcDateTime.AddDays(-1));
        for (var i = 0; i < 3; i++) await SeedAccountAsync(LongAgo);
        var linked = await SeedAccountAsync(LongAgo, linked: true);
        var present = await SeedAccountAsync(Start.UtcDateTime, activeInGuild: true);

        var result = await NewService(new FakeTimeProvider(Start), batchSize: 2)
            .RunOnceAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        result.Should().Be(new DiscordRetentionResult(SyncLogsDeleted: 5, AccountsPurged: 3));
        (await SyncCountAsync()).Should().Be(1);
        (await AccountIdsAsync()).Should().BeEquivalentTo(new[] { linked, present });
    }

    [Fact]
    public async Task EveryBatch_WaitsForTheDiscordWriteGate()
    {
        await SeedSyncAsync(LongAgo);
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();
        var service = NewService(new FakeTimeProvider(Start));
        Task<DiscordRetentionResult> run;

        using (await gate.EnterAsync(CancellationToken.None))
        {
            run = service.RunOnceAsync(CancellationToken.None);
            run.IsCompleted.Should().BeFalse("the gate is held by someone else");
            (await SyncCountAsync()).Should().Be(1);
        }

        (await run.WaitAsync(TimeSpan.FromSeconds(30))).SyncLogsDeleted.Should().Be(1);
        (await SyncCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheFirstPass_FollowsTheStartupDelay_ThenOneComesEveryInterval()
    {
        var time = new ScheduledTimeProvider(Start);
        using var service = NewService(time);
        await SeedSyncAsync(LongAgo);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(time.TimersCreated >= 1));
        time.Advance(service.StartupDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        (await SyncCountAsync()).Should().Be(1, "the startup delay has not elapsed");

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () => await SyncCountAsync() == 0);
        await WaitUntilAsync(() => Task.FromResult(time.TimersCreated >= 2));

        await SeedSyncAsync(LongAgo);
        time.Advance(service.Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        (await SyncCountAsync()).Should().Be(1, "an interval has not passed since the first pass");

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () => await SyncCountAsync() == 0);
        await service.StopAsync(CancellationToken.None);
    }

    private DiscordRetentionService NewService(TimeProvider time, int batchSize = DiscordRetentionService.DefaultBatchSize)
        => new(_provider.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<DiscordWriteGate>(),
            Microsoft.Extensions.Options.Options.Create(new DiscordOptions()),
            time,
            _logger)
        {
            BatchSize = batchSize,
        };

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++) await Task.Delay(50);
        (await condition()).Should().BeTrue("the background pass must finish: {0}; {1}", _logger.Error?.ToString() ?? "no logged failure", _logger.LastMessage);
    }

    private sealed class ProbeLogger : ILogger<DiscordRetentionService>
    {
        public Exception? Error { get; private set; }
        public string? LastMessage { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Error = exception;
            LastMessage = formatter(state, exception);
        }
    }

    /// <summary>Synchronizes fake-clock advances with timer registration on .NET 10's background worker.</summary>
    private sealed class ScheduledTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(start);
        private int _timers;
        public int TimersCreated => Volatile.Read(ref _timers);
        public void Advance(TimeSpan duration) => _clock.Advance(duration);
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    private static string NextId()
        => (400_000_000_000_000_000L + Interlocked.Increment(ref _next)).ToString(CultureInfo.InvariantCulture);

    private async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    private Task SeedSyncAsync(DateTime receivedAt) => WithDbAsync(async db =>
    {
        db.DiscordSyncs.Add(new DiscordSync
        {
            GuildId = GuildId, SubmittedByApiUserId = 1, SubmittedByUsername = "sender",
            ReceivedAt = receivedAt, CollectedAt = receivedAt, DeclaredCollectedAt = receivedAt,
            Method = DiscordSyncMethods.MemberSearch, PluginVersion = "1.0.0",
        });
        await db.SaveChangesAsync();
    });

    private async Task<string> SeedAccountAsync(DateTime lastSeen, bool linked = false, bool activeInGuild = false)
    {
        var userId = NextId();
        await WithDbAsync(async db =>
        {
            db.DiscordAccounts.Add(new DiscordAccount
            {
                DiscordUserId = userId, Username = $"u{userId[^6..]}", FirstSeenAt = lastSeen, LastSeenAt = lastSeen,
            });
            if (activeInGuild)
                db.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = GuildId, DiscordUserId = userId, RoleIdsJson = "[]", FirstSeenAt = lastSeen, LastSeenAt = lastSeen,
                });
            if (linked)
            {
                var entity = new TrackedEntity { CurrentHandle = $"linked{userId[^6..]}", CreatedAt = lastSeen, UpdatedAt = lastSeen };
                db.TrackedEntities.Add(entity);
                await db.SaveChangesAsync();
                db.EntityLinks.Add(new EntityLink
                {
                    TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = userId,
                    AuthorApiUserId = 1, AuthorUsername = "user", CreatedAt = lastSeen, UpdatedAt = lastSeen,
                });
            }
            await db.SaveChangesAsync();
        });
        return userId;
    }

    private async Task<int> SyncCountAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>().DiscordSyncs.CountAsync();
    }

    private async Task<List<string>> AccountIdsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TrackerDbContext>()
            .DiscordAccounts.Select(a => a.DiscordUserId).ToListAsync();
    }
}
