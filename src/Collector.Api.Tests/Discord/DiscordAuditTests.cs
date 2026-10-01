using System.Security.Claims;
using Collector.Api.Auth;
using Collector.Api.Data;
using Collector.Api.Models;
using Collector.Api.Services;
using Collector.Api.Services.Discord;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Spec § 13.2: Discord admin actions are logged after they commit, the static admin key is
/// logged without a user, and a failing log never fails the action.
/// </summary>
public sealed class DiscordAuditTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ApiDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    private static CurrentUserAccessor Actor(string id, string name) => new(new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, name)], "test")),
        },
    });

    [Fact]
    public async Task StaticAdminKey_IsLoggedWithoutAUser()
    {
        await DiscordAudit.LogAsync(new ActivityLogService(_db), Actor("0", "admin"), new ListLogger(),
            DiscordAudit.EraseAccount, "discord_account", "300000000000000001", CancellationToken.None);

        var log = await _db.ActivityLogs.AsNoTracking().SingleAsync();
        log.ApiUserId.Should().BeNull("the static admin key has no api_users row");
        log.Action.Should().Be("discord_erase_account");
        log.EntityType.Should().Be("discord_account");
        log.EntityId.Should().Be("300000000000000001");
    }

    [Fact]
    public async Task SignedInAdmin_IsLoggedWithTheirId()
    {
        var admin = new ApiUser
        {
            Username = "audit-admin", Email = "audit-admin@example.test", PasswordHash = "x", IsAdmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.ApiUsers.Add(admin);
        await _db.SaveChangesAsync();

        await DiscordAudit.LogAsync(new ActivityLogService(_db), Actor(admin.Id.ToString(), admin.Username), new ListLogger(),
            DiscordAudit.EraseGuild, "discord_guild", "123456789012345678", CancellationToken.None);

        (await _db.ActivityLogs.AsNoTracking().SingleAsync()).ApiUserId.Should().Be(admin.Id);
    }

    [Fact]
    public async Task FailedWrite_IsLoggedAsAWarning_AndNeverThrows()
    {
        // A fresh in-memory database on every open: activity_logs does not exist there.
        await using var broken = new ApiDbContext(new DbContextOptionsBuilder<ApiDbContext>().UseSqlite("DataSource=:memory:").Options);
        var logger = new ListLogger();

        var act = () => DiscordAudit.LogAsync(new ActivityLogService(broken), Actor("0", "admin"), logger,
            DiscordAudit.EraseGuild, "discord_guild", "123456789012345678", CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
