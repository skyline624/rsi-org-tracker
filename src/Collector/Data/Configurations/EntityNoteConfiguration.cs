using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class EntityNoteConfiguration : IEntityTypeConfiguration<EntityNote>
{
    public void Configure(EntityTypeBuilder<EntityNote> builder)
    {
        builder.ToTable("entity_notes");

        builder.HasKey(n => n.Id);
        builder.HasIndex(n => n.TrackedEntityId);

        builder.Property(n => n.AuthorUsername)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(n => n.Body).IsRequired();
    }
}
