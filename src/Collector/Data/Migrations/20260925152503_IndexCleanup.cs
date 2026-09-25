using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_organizations_Sid",
                table: "organizations");

            migrationBuilder.DropIndex(
                name: "IX_member_collection_log_CitizenId",
                table: "member_collection_log");

            migrationBuilder.DropIndex(
                name: "IX_member_collection_log_UserHandle",
                table: "member_collection_log");

            // Both indexes were created by hand (or by startup SQL) on the production
            // database already: IF NOT EXISTS makes them formal without failing there.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_change_events_ChangeType_Timestamp"
                ON "change_events" ("ChangeType", "Timestamp" DESC);
                """);

            // Case-insensitive handle index: the API's user search finds roster-only
            // members with a LIKE prefix, which SQLite can only serve from a NOCASE index.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_organization_members_UserHandle_NoCase"
                ON "organization_members" ("UserHandle" COLLATE NOCASE);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_change_events_ChangeType_Timestamp",
                table: "change_events");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_organization_members_UserHandle_NoCase";""");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Sid",
                table: "organizations",
                column: "Sid");

            migrationBuilder.CreateIndex(
                name: "IX_member_collection_log_CitizenId",
                table: "member_collection_log",
                column: "CitizenId");

            migrationBuilder.CreateIndex(
                name: "IX_member_collection_log_UserHandle",
                table: "member_collection_log",
                column: "UserHandle");
        }
    }
}
