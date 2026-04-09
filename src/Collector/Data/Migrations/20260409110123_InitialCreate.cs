using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ChangeType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    OrgSid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UserHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discovered_organizations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UrlImage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UrlCorpo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovered_organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "member_collection_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrgSid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CollectionTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CitizenId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Rank = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RolesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_collection_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrgSid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    UserHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CitizenId = table.Column<int>(type: "INTEGER", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Rank = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RolesJson = table.Column<string>(type: "TEXT", nullable: true),
                    UrlImage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_members", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UrlImage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UrlCorpo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Archetype = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Lang = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Commitment = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Recruiting = table.Column<bool>(type: "INTEGER", nullable: true),
                    Roleplay = table.Column<bool>(type: "INTEGER", nullable: true),
                    MembersCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    History = table.Column<string>(type: "TEXT", nullable: true),
                    Manifesto = table.Column<string>(type: "TEXT", nullable: true),
                    Charter = table.Column<string>(type: "TEXT", nullable: true),
                    FocusPrimaryName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FocusPrimaryImage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FocusSecondaryName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FocusSecondaryImage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ContentCollected = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_enrichment_queue",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Enriched = table.Column<bool>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EnrichedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_enrichment_queue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_handle_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CitizenId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_handle_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CitizenId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UrlImage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Bio = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Enlisted = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_events_ChangeType",
                table: "change_events",
                column: "ChangeType");

            migrationBuilder.CreateIndex(
                name: "IX_change_events_OrgSid_Timestamp",
                table: "change_events",
                columns: new[] { "OrgSid", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_change_events_UserHandle",
                table: "change_events",
                column: "UserHandle");

            migrationBuilder.CreateIndex(
                name: "IX_discovered_organizations_Sid",
                table: "discovered_organizations",
                column: "Sid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_member_collection_log_CitizenId",
                table: "member_collection_log",
                column: "CitizenId");

            migrationBuilder.CreateIndex(
                name: "IX_member_collection_log_OrgSid_CollectionTime",
                table: "member_collection_log",
                columns: new[] { "OrgSid", "CollectionTime" });

            migrationBuilder.CreateIndex(
                name: "IX_member_collection_log_UserHandle",
                table: "member_collection_log",
                column: "UserHandle");

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_CitizenId",
                table: "organization_members",
                column: "CitizenId");

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_OrgSid_IsActive",
                table: "organization_members",
                columns: new[] { "OrgSid", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_OrgSid_Timestamp",
                table: "organization_members",
                columns: new[] { "OrgSid", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_OrgSid_UserHandle",
                table: "organization_members",
                columns: new[] { "OrgSid", "UserHandle" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_UserHandle",
                table: "organization_members",
                column: "UserHandle");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Name",
                table: "organizations",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Sid",
                table: "organizations",
                column: "Sid");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Sid_Timestamp",
                table: "organizations",
                columns: new[] { "Sid", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Timestamp",
                table: "organizations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_user_enrichment_queue_Enriched_Priority_QueuedAt",
                table: "user_enrichment_queue",
                columns: new[] { "Enriched", "Priority", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_enrichment_queue_UserHandle_Pending",
                table: "user_enrichment_queue",
                column: "UserHandle",
                unique: true,
                filter: "\"Enriched\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_user_handle_history_CitizenId",
                table: "user_handle_history",
                column: "CitizenId");

            migrationBuilder.CreateIndex(
                name: "IX_user_handle_history_UserHandle",
                table: "user_handle_history",
                column: "UserHandle");

            migrationBuilder.CreateIndex(
                name: "IX_users_CitizenId",
                table: "users",
                column: "CitizenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_UserHandle",
                table: "users",
                column: "UserHandle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_events");

            migrationBuilder.DropTable(
                name: "discovered_organizations");

            migrationBuilder.DropTable(
                name: "member_collection_log");

            migrationBuilder.DropTable(
                name: "organization_members");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "user_enrichment_queue");

            migrationBuilder.DropTable(
                name: "user_handle_history");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
