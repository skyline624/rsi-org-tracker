using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "api_keys",
                type: "TEXT",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Scope",
                table: "api_keys");
        }
    }
}
