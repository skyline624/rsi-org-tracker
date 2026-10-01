using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordSyncConfiguration : IEntityTypeConfiguration<DiscordSync>
{
    public void Configure(EntityTypeBuilder<DiscordSync> builder)
    {
        builder.ToTable("discord_syncs");

        builder.HasKey(s => s.Id);
        // Preserve the existing SQLite schema while turning the old blocking flag into a signal.
        builder.Property(s => s.MassDepartureDetected).HasColumnName("DepartureGuardTripped");
        // A server's sync log, newest first.
        builder.HasIndex(s => new { s.GuildId, s.Id });

        builder.Property(s => s.GuildId).IsRequired().HasMaxLength(20);
        builder.Property(s => s.SubmittedByUsername).IsRequired().HasMaxLength(100);
        builder.Property(s => s.Method).IsRequired().HasMaxLength(20);
        builder.Property(s => s.PluginVersion).IsRequired().HasMaxLength(20);
    }
}
