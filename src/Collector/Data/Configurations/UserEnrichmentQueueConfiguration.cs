using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class UserEnrichmentQueueConfiguration : IEntityTypeConfiguration<UserEnrichmentQueue>
{
    public void Configure(EntityTypeBuilder<UserEnrichmentQueue> builder)
    {
        builder.ToTable("user_enrichment_queue");

        builder.HasKey(q => q.Id);

        // Index for finding pending items
        builder.HasIndex(q => new { q.Enriched, q.Priority, q.QueuedAt });
        builder.HasIndex(q => q.UserHandle);

        // Partial unique index: prevent duplicate pending entries for the same handle
        builder.HasIndex(q => q.UserHandle)
            .HasFilter("\"Enriched\" = 0")
            .IsUnique()
            .HasDatabaseName("IX_user_enrichment_queue_UserHandle_Pending");

        builder.Property(q => q.UserHandle)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(q => q.LastError)
            .HasMaxLength(2000);
    }
}