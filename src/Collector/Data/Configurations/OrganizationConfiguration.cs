using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);

        // Unique constraint: one snapshot per org per timestamp
        builder.HasIndex(o => new { o.Sid, o.Timestamp }).IsUnique();

        // Query indexes
        builder.HasIndex(o => o.Sid);
        builder.HasIndex(o => o.Timestamp);
        builder.HasIndex(o => o.Name);

        builder.Property(o => o.Sid)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(o => o.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(o => o.UrlImage)
            .HasMaxLength(2000);

        builder.Property(o => o.UrlCorpo)
            .HasMaxLength(2000);

        builder.Property(o => o.Archetype)
            .HasMaxLength(100);

        builder.Property(o => o.Lang)
            .HasMaxLength(50);

        builder.Property(o => o.Commitment)
            .HasMaxLength(100);

        // Extended content fields (Phase 2)
        builder.Property(o => o.Description);
        builder.Property(o => o.History);
        builder.Property(o => o.Manifesto);
        builder.Property(o => o.Charter);
        builder.Property(o => o.FocusPrimaryName).HasMaxLength(200);
        builder.Property(o => o.FocusPrimaryImage).HasMaxLength(2000);
        builder.Property(o => o.FocusSecondaryName).HasMaxLength(200);
        builder.Property(o => o.FocusSecondaryImage).HasMaxLength(2000);
        builder.Property(o => o.ContentCollected).HasDefaultValue(false);
    }
}