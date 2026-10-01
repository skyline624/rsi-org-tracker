using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordRoleConfiguration : IEntityTypeConfiguration<DiscordRole>
{
    public void Configure(EntityTypeBuilder<DiscordRole> builder)
    {
        builder.ToTable("discord_roles");

        builder.HasKey(r => r.Id);
        // Also serves "every role of a server" (GuildId prefix).
        builder.HasIndex(r => new { r.GuildId, r.RoleId }).IsUnique();

        builder.Property(r => r.GuildId).IsRequired().HasMaxLength(20);
        builder.Property(r => r.RoleId).IsRequired().HasMaxLength(20);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Color).HasMaxLength(7);
        builder.Property(r => r.RsiRankLabel).HasMaxLength(100);
    }
}
