using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class OrgNoteConfiguration : IEntityTypeConfiguration<OrgNote>
{
    public void Configure(EntityTypeBuilder<OrgNote> builder)
    {
        builder.ToTable("org_notes");

        builder.HasKey(n => n.Id);
        builder.HasIndex(n => n.OrgSid);

        builder.Property(n => n.OrgSid).IsRequired().HasMaxLength(50);
        builder.Property(n => n.AuthorUsername).IsRequired().HasMaxLength(100);
        builder.Property(n => n.Body).IsRequired();
    }
}
