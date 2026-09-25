using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class OrgMemberCountConfiguration : IEntityTypeConfiguration<OrgMemberCount>
{
    public void Configure(EntityTypeBuilder<OrgMemberCount> builder)
    {
        builder.ToTable("org_member_counts");

        builder.HasKey(c => c.Id);

        // Latest counters of an org, and its history in order.
        builder.HasIndex(c => new { c.OrgSid, c.CollectedAt });

        builder.Property(c => c.OrgSid)
            .IsRequired()
            .HasMaxLength(50);
    }
}
