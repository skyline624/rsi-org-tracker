using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscoveredOrganizationConfiguration : IEntityTypeConfiguration<DiscoveredOrganization>
{
    public void Configure(EntityTypeBuilder<DiscoveredOrganization> builder)
    {
        builder.ToTable("discovered_organizations");

        builder.HasKey(d => d.Id);

        // Unique by SID to avoid duplicates during discovery
        builder.HasIndex(d => d.Sid).IsUnique();

        builder.Property(d => d.Sid)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(d => d.Name)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(d => d.UrlImage)
            .HasMaxLength(2000);

        builder.Property(d => d.UrlCorpo)
            .HasMaxLength(2000);

        builder.Property(d => d.ConsecutiveNotFoundCount)
            .HasDefaultValue(0);

        builder.Property(d => d.DeadAt);

        builder.HasIndex(d => d.DeadAt)
            .HasDatabaseName("IX_discovered_organizations_DeadAt");
    }
}