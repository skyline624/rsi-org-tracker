using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordRosters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discord_accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscordUserId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    GlobalName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsBot = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_guild_optouts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ByApiUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ByUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_guild_optouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_guilds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IconHash = table.Column<string>(type: "TEXT", maxLength: 34, nullable: true),
                    OrgSid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    OrgMappedByApiUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    OrgMappedByUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OrgMappedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MemberCount = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstSyncAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCollectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCompleteSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AllowMassDepartureOnce = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_guilds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_link_rejections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscordUserId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CitizenKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ByApiUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ByUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_link_rejections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_member_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    DiscordUserId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SyncId = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotBefore = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObservedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_member_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_members",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DiscordUserId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Nick = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RoleIdsJson = table.Column<string>(type: "TEXT", maxLength: 6000, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LeftAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_members", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_optouts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscordUserId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ByApiUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ByUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_optouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_roles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    Hoist = table.Column<bool>(type: "INTEGER", nullable: false),
                    Managed = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRank = table.Column<bool>(type: "INTEGER", nullable: false),
                    RankOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    RsiRankLabel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discord_syncs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SubmittedByApiUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    SubmittedByUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeclaredCollectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DeclaredComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    DepartureGuardTripped = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpectedCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CollectedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OptedOutCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnknownRoleRefCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PluginVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discord_syncs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entity_links_Provider_Value",
                table: "entity_links",
                columns: new[] { "Provider", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_discord_accounts_DiscordUserId",
                table: "discord_accounts",
                column: "DiscordUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_guild_optouts_GuildId",
                table: "discord_guild_optouts",
                column: "GuildId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_guilds_GuildId",
                table: "discord_guilds",
                column: "GuildId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_guilds_OrgSid",
                table: "discord_guilds",
                column: "OrgSid");

            migrationBuilder.CreateIndex(
                name: "IX_discord_link_rejections_DiscordUserId_CitizenKey",
                table: "discord_link_rejections",
                columns: new[] { "DiscordUserId", "CitizenKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_member_events_DiscordUserId_Id",
                table: "discord_member_events",
                columns: new[] { "DiscordUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_discord_member_events_GuildId_Id",
                table: "discord_member_events",
                columns: new[] { "GuildId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_discord_member_events_SyncId",
                table: "discord_member_events",
                column: "SyncId");

            migrationBuilder.CreateIndex(
                name: "IX_discord_members_DiscordUserId",
                table: "discord_members",
                column: "DiscordUserId");

            migrationBuilder.CreateIndex(
                name: "IX_discord_members_GuildId_DiscordUserId",
                table: "discord_members",
                columns: new[] { "GuildId", "DiscordUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_members_GuildId_LeftAt",
                table: "discord_members",
                columns: new[] { "GuildId", "LeftAt" });

            migrationBuilder.CreateIndex(
                name: "IX_discord_optouts_DiscordUserId",
                table: "discord_optouts",
                column: "DiscordUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_roles_GuildId_RoleId",
                table: "discord_roles",
                columns: new[] { "GuildId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discord_syncs_GuildId_Id",
                table: "discord_syncs",
                columns: new[] { "GuildId", "Id" });

            // Link suggestions look handles up with UserHandle COLLATE NOCASE IN (…), which
            // SQLite serves only from NOCASE indexes. Raw SQL as in IndexCleanup: EF cannot
            // declare a collation on an index column. IF NOT EXISTS lets an index created by
            // hand on the production database first become formal without failing.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_users_UserHandle_NoCase"
                ON "users" ("UserHandle" COLLATE NOCASE);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_user_handle_history_UserHandle_NoCase"
                ON "user_handle_history" ("UserHandle" COLLATE NOCASE);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_users_UserHandle_NoCase";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_user_handle_history_UserHandle_NoCase";""");

            migrationBuilder.DropTable(
                name: "discord_accounts");

            migrationBuilder.DropTable(
                name: "discord_guild_optouts");

            migrationBuilder.DropTable(
                name: "discord_guilds");

            migrationBuilder.DropTable(
                name: "discord_link_rejections");

            migrationBuilder.DropTable(
                name: "discord_member_events");

            migrationBuilder.DropTable(
                name: "discord_members");

            migrationBuilder.DropTable(
                name: "discord_optouts");

            migrationBuilder.DropTable(
                name: "discord_roles");

            migrationBuilder.DropTable(
                name: "discord_syncs");

            migrationBuilder.DropIndex(
                name: "IX_entity_links_Provider_Value",
                table: "entity_links");
        }
    }
}
