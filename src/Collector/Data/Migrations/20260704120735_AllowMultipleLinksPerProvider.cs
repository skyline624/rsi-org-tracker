using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleLinksPerProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_entity_links_TrackedEntityId_Provider",
                table: "entity_links");

            migrationBuilder.CreateIndex(
                name: "IX_entity_links_TrackedEntityId_Provider_Value",
                table: "entity_links",
                columns: new[] { "TrackedEntityId", "Provider", "Value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_entity_links_TrackedEntityId_Provider_Value",
                table: "entity_links");

            migrationBuilder.CreateIndex(
                name: "IX_entity_links_TrackedEntityId_Provider",
                table: "entity_links",
                columns: new[] { "TrackedEntityId", "Provider" },
                unique: true);
        }
    }
}
