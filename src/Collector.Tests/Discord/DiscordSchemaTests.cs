using Collector.Data;
using Collector.Extensions;
using Collector.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Discord;

/// <summary>
/// The discord_* tables built by the migration chain: every entity round-trips with its
/// dates still UTC, and the natural keys reject duplicates.
/// </summary>
public sealed class DiscordSchemaTests : IAsyncLifetime
{
    private const string GuildId = "100000000000000001";
    private const string RoleId = "300000000000000001";
    private const string UserId = "200000000000000001";
    private static readonly DateTime At = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<TrackerDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await _provider.EnsureDatabaseAsync(Path.GetTempPath());
    }

    private TrackerDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<TrackerDbContext>();

    /// <summary>One of each entity, every column set.</summary>
    private static IEnumerable<object> EveryEntity() =>
    [
        new DiscordGuild
        {
            GuildId = GuildId, Name = "Ma Corpo", IconHash = "a_" + new string('f', 32), OrgSid = "CORP",
            OrgMappedByApiUserId = 7, OrgMappedByUsername = "officer", OrgMappedAt = At, MemberCount = 1234,
            FirstSyncAt = At, LastSyncAt = At.AddMinutes(2), LastCollectedAt = At.AddMinutes(1),
            LastCompleteSyncAt = At, AllowMassDepartureOnce = true, CreatedAt = At, UpdatedAt = At,
        },
        new DiscordRole
        {
            GuildId = GuildId, RoleId = RoleId, Name = "Officier", Position = 12, Color = "#e67e22",
            Hoist = true, Managed = false, IsRank = true, RankOrder = 12, RsiRankLabel = "Officer",
            FirstSeenAt = At, LastSeenAt = At, DeletedAt = At.AddDays(1),
        },
        new DiscordAccount
        {
            DiscordUserId = UserId, Username = "pilote42", GlobalName = "Pilote", IsBot = false,
            FirstSeenAt = At, LastSeenAt = At,
        },
        new DiscordMember
        {
            GuildId = GuildId, DiscordUserId = UserId, Nick = "[CORP] Pilote42", RoleIdsJson = $"[\"{RoleId}\"]",
            JoinedAt = At.AddYears(-1), FirstSeenAt = At, LastSeenAt = At, LeftAt = At.AddDays(3),
        },
        new DiscordMemberEvent
        {
            GuildId = GuildId, DiscordUserId = UserId, SyncId = 42, Type = DiscordEventTypes.RolesChanged,
            OldValue = "[]", NewValue = $"[{{\"id\":\"{RoleId}\",\"name\":\"Officier\"}}]",
            OccurredAt = At, NotBefore = At.AddDays(-1), ObservedAt = At,
        },
        new DiscordSync
        {
            GuildId = GuildId, SubmittedByApiUserId = 7, SubmittedByUsername = "officer",
            ReceivedAt = At.AddMinutes(2), CollectedAt = At.AddMinutes(1), DeclaredCollectedAt = At,
            Method = DiscordSyncMethods.MemberSearch, DeclaredComplete = true, IsComplete = true,
            IsBaseline = true, MassDepartureDetected = false, ExpectedCount = 1234, CollectedCount = 1234,
            OptedOutCount = 2, UnknownRoleRefCount = 1, EventCount = 3, PluginVersion = "1.0.0",
        },
        new DiscordOptOut { DiscordUserId = UserId, CreatedAt = At, ByApiUserId = null, ByUsername = "admin", Reason = "request" },
        new DiscordGuildOptOut { GuildId = GuildId, CreatedAt = At, ByApiUserId = 7, ByUsername = "officer", Reason = "no consent" },
        new DiscordLinkRejection { DiscordUserId = UserId, CitizenKey = "h:pilote42", ByApiUserId = 7, ByUsername = "officer", CreatedAt = At },
    ];

    [Fact]
    public async Task EveryDiscordEntity_RoundTrips_WithUtcDates()
    {
        foreach (var entity in EveryEntity())
        {
            var type = entity.GetType();
            var write = NewDb();
            write.Add(entity);
            await write.SaveChangesAsync();
            var id = (long)type.GetProperty("Id")!.GetValue(entity)!;

            var stored = await NewDb().FindAsync(type, id);

            stored.Should().BeEquivalentTo(entity, o => o.RespectingRuntimeTypes(), type.Name);
            foreach (var property in type.GetProperties().Where(p => p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?)))
            {
                if (property.GetValue(stored) is DateTime value)
                {
                    value.Kind.Should().Be(DateTimeKind.Utc, $"{type.Name}.{property.Name} is read back as UTC");
                }
            }
        }
    }

    [Fact]
    public async Task NaturalKeys_RejectDuplicates()
    {
        var makers = new Func<object>[]
        {
            () => new DiscordGuild { GuildId = GuildId, Name = "G", FirstSyncAt = At, LastSyncAt = At, LastCollectedAt = At, CreatedAt = At, UpdatedAt = At },
            () => new DiscordRole { GuildId = GuildId, RoleId = RoleId, Name = "R", FirstSeenAt = At, LastSeenAt = At },
            () => new DiscordAccount { DiscordUserId = UserId, Username = "u", FirstSeenAt = At, LastSeenAt = At },
            () => new DiscordMember { GuildId = GuildId, DiscordUserId = UserId, FirstSeenAt = At, LastSeenAt = At },
            () => new DiscordOptOut { DiscordUserId = UserId, CreatedAt = At, ByUsername = "admin" },
            () => new DiscordGuildOptOut { GuildId = GuildId, CreatedAt = At, ByUsername = "admin" },
            () => new DiscordLinkRejection { DiscordUserId = UserId, CitizenKey = "h:pilot", ByApiUserId = 1, ByUsername = "u", CreatedAt = At },
        };

        foreach (var make in makers)
        {
            var first = NewDb();
            first.Add(make());
            await first.SaveChangesAsync();

            var second = NewDb();
            second.Add(make());
            var act = () => second.SaveChangesAsync();

            await act.Should().ThrowAsync<DbUpdateException>(make().GetType().Name + " has a unique natural key");
        }
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
