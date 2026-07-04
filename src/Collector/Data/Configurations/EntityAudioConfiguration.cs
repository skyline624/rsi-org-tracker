using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class EntityAudioConfiguration : IEntityTypeConfiguration<EntityAudio>
{
    public void Configure(EntityTypeBuilder<EntityAudio> builder)
    {
        builder.ToTable("entity_audio");

        builder.HasKey(a => a.Id);
        builder.HasIndex(a => a.TrackedEntityId);

        builder.Property(a => a.AuthorUsername).IsRequired().HasMaxLength(100);
        builder.Property(a => a.OriginalName).IsRequired().HasMaxLength(500);
        builder.Property(a => a.StoredPath).IsRequired().HasMaxLength(500);
        builder.Property(a => a.MimeType).IsRequired().HasMaxLength(100);
    }
}
