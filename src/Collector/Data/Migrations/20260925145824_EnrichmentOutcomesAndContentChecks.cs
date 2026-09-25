using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnrichmentOutcomesAndContentChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "user_enrichment_queue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "user_enrichment_queue",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContentCheckedAt",
                table: "discovered_organizations",
                type: "TEXT",
                nullable: true);

            // Rows parked as pending forever become terminal, which frees their handle:
            // "gone" (404) rows carried AttemptCount = int.MaxValue, abandoned rows had
            // reached the default MaxEnrichmentAttempts (3).
            migrationBuilder.Sql("""
                UPDATE user_enrichment_queue
                SET Enriched = 1, Outcome = 'gone', EnrichedAt = COALESCE(EnrichedAt, QueuedAt)
                WHERE Enriched = 0 AND AttemptCount = 2147483647;
                """);
            migrationBuilder.Sql("""
                UPDATE user_enrichment_queue
                SET Enriched = 1, Outcome = 'abandoned', EnrichedAt = COALESCE(EnrichedAt, QueuedAt)
                WHERE Enriched = 0 AND AttemptCount >= 3;
                """);

            // "n/a" profiles were re-read on every pass: spread their next check over
            // the coming 14 days instead of all at once.
            migrationBuilder.Sql("""
                UPDATE user_enrichment_queue
                SET Outcome = 'na',
                    NextAttemptAt = strftime('%Y-%m-%d %H:%M:%S', 'now',
                        '+' || (abs(random()) % 1209600) || ' seconds')
                WHERE Enriched = 0 AND Outcome IS NULL AND LastError = 'No citizen record (n/a)';
                """);

            // Phase 2 now reads ContentCheckedAt to find stale orgs: start from the
            // latest content snapshot so the first cycle does not re-read every page.
            migrationBuilder.Sql("""
                UPDATE discovered_organizations
                SET ContentCheckedAt = (
                    SELECT MAX(o.Timestamp) FROM organizations o
                    WHERE o.Sid = discovered_organizations.Sid AND o.ContentCollected = 1)
                WHERE ContentCheckedAt IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "user_enrichment_queue");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "user_enrichment_queue");

            migrationBuilder.DropColumn(
                name: "ContentCheckedAt",
                table: "discovered_organizations");
        }
    }
}
