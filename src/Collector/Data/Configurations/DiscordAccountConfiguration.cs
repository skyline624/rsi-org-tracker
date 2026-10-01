using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordAccountConfiguration : IEntityTypeConfiguration<DiscordAccount>
{
    public void Configure(EntityTypeBuilder<DiscordAccount> builder)
    {
        builder.ToTable("discord_accounts");

        builder.HasKey(a => a.Id);
        builder.HasIndex(a => a.DiscordUserId).IsUnique();

        builder.Property(a => a.DiscordUserId).IsRequired().HasMaxLength(20);
        builder.Property(a => a.Username).IsRequired().HasMaxLength(32);
        builder.Property(a => a.GlobalName).HasMaxLength(32);
    }
}
