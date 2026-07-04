using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entity_audio",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackedEntityId = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorApiUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSec = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_audio", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entity_audio_TrackedEntityId",
                table: "entity_audio",
                column: "TrackedEntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_audio");
        }
    }
}
