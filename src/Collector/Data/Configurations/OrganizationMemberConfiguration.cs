using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("organization_members");

        builder.HasKey(m => m.Id);

        // Index for querying by organization and time
        builder.HasIndex(m => new { m.OrgSid, m.Timestamp });
        builder.HasIndex(m => new { m.OrgSid, m.UserHandle });
        builder.HasIndex(m => m.UserHandle);
        builder.HasIndex(m => m.CitizenId);

        builder.Property(m => m.OrgSid)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(m => m.UserHandle)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(m => m.DisplayName)
            .HasMaxLength(200);

        builder.Property(m => m.Rank)
            .HasMaxLength(100);

        builder.Property(m => m.UrlImage)
            .HasMaxLength(2000);

        builder.Property(m => m.IsActive)
            .HasDefaultValue(true);

        builder.HasIndex(m => new { m.OrgSid, m.IsActive });
    }
}