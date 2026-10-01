using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordLinkRejectionConfiguration : IEntityTypeConfiguration<DiscordLinkRejection>
{
    public void Configure(EntityTypeBuilder<DiscordLinkRejection> builder)
    {
        builder.ToTable("discord_link_rejections");

        builder.HasKey(r => r.Id);
        // One rejection per (account, citizen); also serves "every rejection of an account".
        builder.HasIndex(r => new { r.DiscordUserId, r.CitizenKey }).IsUnique();

        builder.Property(r => r.DiscordUserId).IsRequired().HasMaxLength(20);
        builder.Property(r => r.CitizenKey).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ByUsername).IsRequired().HasMaxLength(100);
    }
}
