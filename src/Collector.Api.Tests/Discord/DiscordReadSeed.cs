using System.Text.Json;
using Collector.Data;
using Collector.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// Seeds tracker.db straight through TrackerDbContext for the Discord read tests: RSI orgs,
/// rosters and counters, tracked people with their discord links, and guilds, roles,
/// accounts and members as the ingest leaves them. Every row is dated from <see cref="At"/>
/// so dates can be asserted exactly; ids come from DiscordTestKit.NewSnowflake().
/// </summary>
public sealed class DiscordReadSeed(ApiFactory factory)
{
    public static readonly DateTime At = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    public async Task WithDbAsync(Func<TrackerDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    public async Task<T> ReadAsync<T>(Func<TrackerDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TrackerDbContext>());
    }

    public Task SeedOrgAsync(string sid, string name) => WithDbAsync(async db =>
    {
        db.Organizations.Add(new Organization { Sid = sid, Name = name, Timestamp = At });
        await db.SaveChangesAsync();
    });

    /// <summary>
    /// One roster row. IsActive has a database default of true, so EF takes an inserted false
    /// for "unset": an inactive row is inserted active, then updated.
    /// </summary>
    public Task SeedRosterAsync(string sid, string handle, int? citizenId, string? rank, bool active = true, int? stars = null)
        => WithDbAsync(async db =>
        {
            var row = new OrganizationMember
            {
                OrgSid = sid, UserHandle = handle, CitizenId = citizenId, Rank = rank, Stars = stars,
                Timestamp = At, IsActive = true,
            };
            db.OrganizationMembers.Add(row);
            await db.SaveChangesAsync();
            if (active) return;
            row.IsActive = false;
            await db.SaveChangesAsync();
        });

    public Task SeedCountsAsync(string sid, DateTime collectedAt, int totalRows, int? visible, int? redacted, int? hidden)
        => WithDbAsync(async db =>
        {
            db.OrgMemberCounts.Add(new OrgMemberCount
            {
                OrgSid = sid, CollectedAt = collectedAt, TotalRows = totalRows,
                VisibleCount = visible, RedactedCount = redacted, HiddenCount = hidden,
            });
            await db.SaveChangesAsync();
        });

    /// <summary>A tracked person, linked to each of <paramref name="discordUserIds"/>. Returns the entity id.</summary>
    public Task<long> SeedPersonAsync(int? citizenId, string handle, string? displayName, params string[] discordUserIds)
        => ReadAsync(async db =>
        {
            var entity = new TrackedEntity
            {
                CitizenId = citizenId, CurrentHandle = handle, DisplayName = displayName, CreatedAt = At, UpdatedAt = At,
            };
            db.TrackedEntities.Add(entity);
            await db.SaveChangesAsync();
            foreach (var discordUserId in discordUserIds)
            {
                db.EntityLinks.Add(new EntityLink
                {
                    TrackedEntityId = entity.Id, Provider = LinkProviders.Discord, Value = discordUserId,
                    AuthorApiUserId = 0, AuthorUsername = "seed", CreatedAt = At, UpdatedAt = At,
                });
            }
            await db.SaveChangesAsync();
            return entity.Id;
        });

    /// <summary>
    /// A guild synced at <see cref="At"/>. <paramref name="complete"/> sets LastCompleteSyncAt
    /// to the last sync's CollectedAt (At), otherwise leaves it null.
    /// </summary>
    public async Task<string> SeedGuildAsync(
        string? orgSid, string? name = null, bool complete = true, long? mappedByApiUserId = null, string? mappedByUsername = null)
    {
        var guildId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            db.DiscordGuilds.Add(new DiscordGuild
            {
                GuildId = guildId,
                Name = name ?? $"Guild {guildId}",
                OrgSid = orgSid,
                OrgMappedByApiUserId = orgSid is null ? null : mappedByApiUserId,
                OrgMappedByUsername = orgSid is null ? null : mappedByUsername,
                OrgMappedAt = orgSid is null ? null : At,
                FirstSyncAt = At.AddDays(-30),
                LastSyncAt = At,
                LastCollectedAt = At,
                LastCompleteSyncAt = complete ? At : null,
                CreatedAt = At,
                UpdatedAt = At,
            });
            await db.SaveChangesAsync();
        });
        return guildId;
    }

    /// <summary>A role of the guild; a rank without an explicit order takes its position, as on ingest.</summary>
    public async Task<string> SeedRoleAsync(
        string guildId, string name, int position, bool isRank, int? rankOrder = null, string? rsiRankLabel = null,
        bool deleted = false, string? color = "#e67e22")
    {
        var roleId = DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            db.DiscordRoles.Add(new DiscordRole
            {
                GuildId = guildId, RoleId = roleId, Name = name, Position = position, Color = color,
                Hoist = isRank, Managed = false, IsRank = isRank,
                RankOrder = isRank ? rankOrder ?? position : rankOrder,
                RsiRankLabel = rsiRankLabel, FirstSeenAt = At, LastSeenAt = At, DeletedAt = deleted ? At : null,
            });
            await db.SaveChangesAsync();
        });
        return roleId;
    }

    /// <summary>
    /// A member row of the guild and, unless it exists already (<paramref name="userId"/> of an
    /// account seeded before), its account. Returns the Discord user id.
    /// </summary>
    public async Task<string> SeedMemberAsync(
        string guildId, string username, IEnumerable<string>? roleIds = null, string? nick = null, string? globalName = null,
        bool bot = false, bool left = false, string? userId = null, DateTime? joinedAt = null)
    {
        var id = userId ?? DiscordTestKit.NewSnowflake();
        await WithDbAsync(async db =>
        {
            if (!await db.DiscordAccounts.AnyAsync(a => a.DiscordUserId == id))
            {
                db.DiscordAccounts.Add(new DiscordAccount
                {
                    DiscordUserId = id, Username = username, GlobalName = globalName, IsBot = bot, FirstSeenAt = At, LastSeenAt = At,
                });
            }
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guildId,
                DiscordUserId = id,
                Nick = nick,
                RoleIdsJson = JsonSerializer.Serialize((roleIds ?? []).Order(StringComparer.Ordinal).ToArray()),
                JoinedAt = joinedAt,
                FirstSeenAt = At,
                LastSeenAt = At,
                LeftAt = left ? At : null,
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    /// <summary>Re-maps a guild without going through the API (the PUT route arrives in C6).</summary>
    public Task SetGuildOrgAsync(string guildId, string? orgSid) => WithDbAsync(async db =>
    {
        var guild = await db.DiscordGuilds.SingleAsync(g => g.GuildId == guildId);
        guild.OrgSid = orgSid;
        guild.UpdatedAt = At;
        await db.SaveChangesAsync();
    });
}
