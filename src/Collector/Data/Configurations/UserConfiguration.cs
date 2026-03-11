using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        // citizen_id is unique and permanent
        builder.HasIndex(u => u.CitizenId).IsUnique();
        builder.HasIndex(u => u.UserHandle);

        builder.Property(u => u.CitizenId)
            .IsRequired();

        builder.Property(u => u.UserHandle)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(u => u.DisplayName)
            .HasMaxLength(200);

        builder.Property(u => u.UrlImage)
            .HasMaxLength(2000);

        builder.Property(u => u.Bio)
            .HasMaxLength(5000);

        builder.Property(u => u.Location)
            .HasMaxLength(200);
    }
}