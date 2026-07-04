using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class EntityMembershipConfiguration : IEntityTypeConfiguration<EntityMembership>
{
    public void Configure(EntityTypeBuilder<EntityMembership> builder)
    {
        builder.ToTable("entity_memberships");

        builder.HasKey(m => m.Id);
        builder.HasIndex(m => m.TrackedEntityId);
        builder.HasIndex(m => m.OrgSid);

        // One manual link per (person, org) — re-attaching updates the existing row.
        builder.HasIndex(m => new { m.TrackedEntityId, m.OrgSid }).IsUnique();

        builder.Property(m => m.OrgSid).IsRequired().HasMaxLength(50);
        builder.Property(m => m.Rank).HasMaxLength(200);
        builder.Property(m => m.Via).IsRequired().HasMaxLength(20).HasDefaultValue(MembershipVia.Discord);
        builder.Property(m => m.AuthorUsername).IsRequired().HasMaxLength(100);
    }
}
