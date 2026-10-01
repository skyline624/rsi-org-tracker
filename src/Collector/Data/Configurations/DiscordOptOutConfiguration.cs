using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordOptOutConfiguration : IEntityTypeConfiguration<DiscordOptOut>
{
    public void Configure(EntityTypeBuilder<DiscordOptOut> builder)
    {
        builder.ToTable("discord_optouts");

        builder.HasKey(o => o.Id);
        builder.HasIndex(o => o.DiscordUserId).IsUnique();

        builder.Property(o => o.DiscordUserId).IsRequired().HasMaxLength(20);
        builder.Property(o => o.ByUsername).IsRequired().HasMaxLength(100);
        builder.Property(o => o.Reason).HasMaxLength(500);
    }
}
