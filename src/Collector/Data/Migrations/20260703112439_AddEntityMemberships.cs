using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entity_memberships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedEntityId = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgSid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Rank = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Via = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "discord"),
                    SinceDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuthorApiUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_memberships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entity_memberships_OrgSid",
                table: "entity_memberships",
                column: "OrgSid");

            migrationBuilder.CreateIndex(
                name: "IX_entity_memberships_TrackedEntityId",
                table: "entity_memberships",
                column: "TrackedEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_entity_memberships_TrackedEntityId_OrgSid",
                table: "entity_memberships",
                columns: new[] { "TrackedEntityId", "OrgSid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_memberships");
        }
    }
}
