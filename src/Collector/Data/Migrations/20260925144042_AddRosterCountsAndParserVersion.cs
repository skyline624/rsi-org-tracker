using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRosterCountsAndParserVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Stars",
                table: "organization_members",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParserVersion",
                table: "member_collection_log",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "org_member_counts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrgSid = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalRows = table.Column<int>(type: "INTEGER", nullable: false),
                    VisibleCount = table.Column<int>(type: "INTEGER", nullable: true),
                    RedactedCount = table.Column<int>(type: "INTEGER", nullable: true),
                    HiddenCount = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_member_counts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_org_member_counts_OrgSid_CollectedAt",
                table: "org_member_counts",
                columns: new[] { "OrgSid", "CollectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "org_member_counts");

            migrationBuilder.DropColumn(
                name: "Stars",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "ParserVersion",
                table: "member_collection_log");
        }
    }
}
