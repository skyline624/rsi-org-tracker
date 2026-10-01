using Collector.Data;
using Collector.Extensions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Tests.Data;

/// <summary>
/// The indexes tracker.db must carry, created by migrations rather than raw SQL at
/// startup, and the ones dropped because no query uses them.
/// </summary>
public sealed class IndexSetTests : IAsyncLifetime
{
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

    private async Task<List<string>> IndexesAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        return await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_%'").ToListAsync();
    }

    [Fact]
    public async Task QueriedIndexes_Exist()
    {
        (await IndexesAsync()).Should().Contain([
            "IX_organization_members_UserHandle_NoCase", // user search by handle prefix
            "IX_change_events_ChangeType_Timestamp",     // /changes summary (skip-scan over types)
            "IX_change_events_ChangeType",               // /changes by type, newest Id first
            "IX_discovered_organizations_DeadAt",
        ]);
    }

    [Fact]
    public async Task UnusedIndexes_AreGone()
    {
        (await IndexesAsync()).Should().NotContain([
            "IX_organizations_Sid",                  // prefix of the unique (Sid, Timestamp)
            "IX_member_collection_log_CitizenId",
            "IX_member_collection_log_UserHandle",
        ]);
    }

    [Fact]
    public async Task TheNoCaseIndex_ComesFromAMigration_NotFromStartupSql()
    {
        var bootstrap = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Collector", "Extensions", "ServiceCollectionExtensions.cs"));

        bootstrap.Should().NotContain("IX_organization_members_UserHandle_NoCase");
    }

    private async Task<string> IndexSqlAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        return await db.Database.SqlQueryRaw<string>(
            "SELECT sql AS Value FROM sqlite_master WHERE type = 'index' AND name = {0}", name).SingleAsync();
    }

    [Fact]
    public async Task DiscordRosterIndexes_Exist()
    {
        (await IndexesAsync()).Should().Contain([
            "IX_users_UserHandle_NoCase",                 // link suggestions: UserHandle COLLATE NOCASE IN (…)
            "IX_user_handle_history_UserHandle_NoCase",
            "IX_entity_links_Provider_Value",             // the people linked to a Discord id
            "IX_discord_guilds_OrgSid",
            "IX_discord_members_DiscordUserId",
            "IX_discord_members_GuildId_LeftAt",
            "IX_discord_member_events_GuildId_Id",
            "IX_discord_member_events_DiscordUserId_Id",
            "IX_discord_member_events_SyncId",
            "IX_discord_syncs_GuildId_Id",
        ]);
    }

    [Theory]
    [InlineData("IX_discord_guilds_GuildId")]
    [InlineData("IX_discord_roles_GuildId_RoleId")]
    [InlineData("IX_discord_accounts_DiscordUserId")]
    [InlineData("IX_discord_members_GuildId_DiscordUserId")]
    [InlineData("IX_discord_optouts_DiscordUserId")]
    [InlineData("IX_discord_guild_optouts_GuildId")]
    [InlineData("IX_discord_link_rejections_DiscordUserId_CitizenKey")]
    public async Task DiscordNaturalKeys_AreUniqueIndexes(string index)
    {
        (await IndexSqlAsync(index)).Should().StartWith("CREATE UNIQUE INDEX");
    }

    [Theory]
    [InlineData("IX_users_UserHandle_NoCase")]
    [InlineData("IX_user_handle_history_UserHandle_NoCase")]
    public async Task HandleIndexesForLinkSuggestions_IgnoreCase(string index)
    {
        (await IndexSqlAsync(index)).Should().Contain("COLLATE NOCASE");
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _connection.Dispose();
    }
}
