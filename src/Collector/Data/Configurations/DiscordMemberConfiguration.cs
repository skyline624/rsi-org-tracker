using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordMemberConfiguration : IEntityTypeConfiguration<DiscordMember>
{
    public void Configure(EntityTypeBuilder<DiscordMember> builder)
    {
        builder.ToTable("discord_members");

        builder.HasKey(m => m.Id);
        builder.HasIndex(m => new { m.GuildId, m.DiscordUserId }).IsUnique();
        // Every server an account is or was on (multi-membership, cross profile, erasure).
        builder.HasIndex(m => m.DiscordUserId);
        // Active (LeftAt null) or former members of a server.
        builder.HasIndex(m => new { m.GuildId, m.LeftAt });

        builder.Property(m => m.GuildId).IsRequired().HasMaxLength(20);
        builder.Property(m => m.DiscordUserId).IsRequired().HasMaxLength(20);
        builder.Property(m => m.Nick).HasMaxLength(32);
        // At most 250 role ids of 20 digits, quoted and comma-separated: 5,751 characters.
        builder.Property(m => m.RoleIdsJson).IsRequired().HasMaxLength(6000);
    }
}
