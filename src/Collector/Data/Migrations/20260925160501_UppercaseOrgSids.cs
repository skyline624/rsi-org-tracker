using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collector.Data.Migrations
{
    /// <inheritdoc />
    public partial class UppercaseOrgSids : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Org SIDs are upper case everywhere; notes and manual memberships written
            // before the API normalized them may not be. A membership whose upper-case
            // twin already exists for the same person is left as it is (unique index).
            migrationBuilder.Sql("""
                UPDATE org_notes SET OrgSid = UPPER(OrgSid) WHERE OrgSid <> UPPER(OrgSid);
                """);
            migrationBuilder.Sql("""
                UPDATE entity_memberships SET OrgSid = UPPER(OrgSid)
                WHERE OrgSid <> UPPER(OrgSid)
                  AND NOT EXISTS (SELECT 1 FROM entity_memberships twin
                                  WHERE twin.TrackedEntityId = entity_memberships.TrackedEntityId
                                    AND twin.OrgSid = UPPER(entity_memberships.OrgSid));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
