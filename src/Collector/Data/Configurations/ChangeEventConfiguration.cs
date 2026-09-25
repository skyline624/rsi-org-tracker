using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class ChangeEventConfiguration : IEntityTypeConfiguration<ChangeEvent>
{
    public void Configure(EntityTypeBuilder<ChangeEvent> builder)
    {
        builder.ToTable("change_events");

        builder.HasKey(c => c.Id);

        // Index for querying by org and time
        builder.HasIndex(c => new { c.OrgSid, c.Timestamp });
        // WHERE ChangeType = ? ORDER BY Id DESC reads this index in rowid order.
        builder.HasIndex(c => c.ChangeType);
        // The /changes summary (Timestamp range, GROUP BY ChangeType) skip-scans it as a
        // covering index; created by hand in production first, hence IF NOT EXISTS in
        // the migration.
        builder.HasIndex(c => new { c.ChangeType, c.Timestamp })
            .IsDescending(false, true)
            .HasDatabaseName("IX_change_events_ChangeType_Timestamp");
        builder.HasIndex(c => c.UserHandle);

        builder.Property(c => c.EntityType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(c => c.EntityId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(c => c.ChangeType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(c => c.OldValue)
            .HasMaxLength(10000);

        builder.Property(c => c.NewValue)
            .HasMaxLength(10000);

        builder.Property(c => c.OrgSid)
            .HasMaxLength(50);

        builder.Property(c => c.UserHandle)
            .HasMaxLength(100);
    }
}