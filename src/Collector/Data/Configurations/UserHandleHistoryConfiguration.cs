using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class UserHandleHistoryConfiguration : IEntityTypeConfiguration<UserHandleHistory>
{
    public void Configure(EntityTypeBuilder<UserHandleHistory> builder)
    {
        builder.ToTable("user_handle_history");

        builder.HasKey(h => h.Id);

        // Index for querying handle history by citizen_id
        builder.HasIndex(h => h.CitizenId);
        builder.HasIndex(h => h.UserHandle);

        builder.Property(h => h.CitizenId)
            .IsRequired();

        builder.Property(h => h.UserHandle)
            .IsRequired()
            .HasMaxLength(100);
    }
}