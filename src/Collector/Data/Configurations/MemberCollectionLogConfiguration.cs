using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class MemberCollectionLogConfiguration : IEntityTypeConfiguration<MemberCollectionLog>
{
    public void Configure(EntityTypeBuilder<MemberCollectionLog> builder)
    {
        builder.ToTable("member_collection_log");

        builder.HasKey(m => m.Id);

        // Index for querying logs by org and time
        builder.HasIndex(m => new { m.OrgSid, m.CollectionTime });
        builder.HasIndex(m => m.CitizenId);
        builder.HasIndex(m => m.UserHandle);

        builder.Property(m => m.OrgSid)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(m => m.UserHandle)
            .IsRequired()
            .HasMaxLength(100);
    }
}