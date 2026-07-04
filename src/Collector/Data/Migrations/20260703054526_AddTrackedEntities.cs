using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: only tracked_entities is created here. EF's model snapshot had drifted
            // (discovered_organizations already has ConsecutiveNotFoundCount/DeadAt + index
            // in prod), so the auto-generated AddColumn/CreateIndex for that table were
            // removed to avoid "duplicate column" errors. The snapshot now matches reality.
            migrationBuilder.CreateTable(
                name: "tracked_entities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CitizenId = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentHandle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "collected"),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "active"),
                    MergedIntoId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_entities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_entities_CitizenId",
                table: "tracked_entities",
                column: "CitizenId",
                unique: true,
                filter: "\"CitizenId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tracked_entities_CurrentHandle",
                table: "tracked_entities",
                column: "CurrentHandle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracked_entities");
        }
    }
}
