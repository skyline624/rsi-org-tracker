using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class EntityLinkConfiguration : IEntityTypeConfiguration<EntityLink>
{
    public void Configure(EntityTypeBuilder<EntityLink> builder)
    {
        builder.ToTable("entity_links");

        builder.HasKey(l => l.Id);
        // Several links per provider are allowed (e.g. multiple Discord accounts);
        // only exact (entity, provider, value) duplicates are rejected.
        builder.HasIndex(l => new { l.TrackedEntityId, l.Provider, l.Value }).IsUnique();
        // Look up all people linked to a Discord user without scanning links by entity.
        builder.HasIndex(l => new { l.Provider, l.Value });

        builder.Property(l => l.Provider).IsRequired().HasMaxLength(30);
        builder.Property(l => l.Value).IsRequired().HasMaxLength(200);
        builder.Property(l => l.AuthorUsername).IsRequired().HasMaxLength(100);
    }
}
