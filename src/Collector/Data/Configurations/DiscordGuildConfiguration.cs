using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordGuildConfiguration : IEntityTypeConfiguration<DiscordGuild>
{
    public void Configure(EntityTypeBuilder<DiscordGuild> builder)
    {
        builder.ToTable("discord_guilds");

        builder.HasKey(g => g.Id);
        builder.HasIndex(g => g.GuildId).IsUnique();
        // Not unique: an organization can have several servers (main, recruitment…).
        builder.HasIndex(g => g.OrgSid);

        builder.Property(g => g.GuildId).IsRequired().HasMaxLength(20);
        builder.Property(g => g.Name).IsRequired().HasMaxLength(100);
        builder.Property(g => g.IconHash).HasMaxLength(34);
        builder.Property(g => g.OrgSid).HasMaxLength(50);
        builder.Property(g => g.OrgMappedByUsername).HasMaxLength(100);
    }
}
