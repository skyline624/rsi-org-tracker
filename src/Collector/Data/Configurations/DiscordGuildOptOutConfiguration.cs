using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordGuildOptOutConfiguration : IEntityTypeConfiguration<DiscordGuildOptOut>
{
    public void Configure(EntityTypeBuilder<DiscordGuildOptOut> builder)
    {
        builder.ToTable("discord_guild_optouts");

        builder.HasKey(o => o.Id);
        builder.HasIndex(o => o.GuildId).IsUnique();

        builder.Property(o => o.GuildId).IsRequired().HasMaxLength(20);
        builder.Property(o => o.ByUsername).IsRequired().HasMaxLength(100);
        builder.Property(o => o.Reason).HasMaxLength(500);
    }
}
