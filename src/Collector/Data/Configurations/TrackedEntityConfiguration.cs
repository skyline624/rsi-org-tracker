using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class TrackedEntityConfiguration : IEntityTypeConfiguration<TrackedEntity>
{
    public void Configure(EntityTypeBuilder<TrackedEntity> builder)
    {
        builder.ToTable("tracked_entities");

        builder.HasKey(e => e.Id);

        // At most one entity per *known* citizen id. NULL rows (redacted / roster-only)
        // are excluded from the constraint via the filter, so many may coexist.
        builder.HasIndex(e => e.CitizenId)
            .HasDatabaseName("IX_tracked_entities_CitizenId")
            .HasFilter("\"CitizenId\" IS NOT NULL")
            .IsUnique();

        builder.HasIndex(e => e.CurrentHandle)
            .HasDatabaseName("IX_tracked_entities_CurrentHandle");

        builder.Property(e => e.CurrentHandle).HasMaxLength(100);
        builder.Property(e => e.DisplayName).HasMaxLength(500);

        builder.Property(e => e.Source)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue(TrackedEntitySource.Collected);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue(TrackedEntityStatus.Active);
    }
}
