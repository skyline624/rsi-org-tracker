using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Collector.Models;

namespace Collector.Data.Configurations;

public class DiscordMemberEventConfiguration : IEntityTypeConfiguration<DiscordMemberEvent>
{
    public void Configure(EntityTypeBuilder<DiscordMemberEvent> builder)
    {
        builder.ToTable("discord_member_events");

        builder.HasKey(e => e.Id);
        // A server's history and an account's history, newest Id first.
        builder.HasIndex(e => new { e.GuildId, e.Id });
        builder.HasIndex(e => new { e.DiscordUserId, e.Id });
        // Events of a sync (submitter shown on each event, sync log purge).
        builder.HasIndex(e => e.SyncId);

        builder.Property(e => e.GuildId).HasMaxLength(20);
        builder.Property(e => e.DiscordUserId).IsRequired().HasMaxLength(20);
        builder.Property(e => e.Type).IsRequired().HasMaxLength(30);
        builder.Property(e => e.OldValue).HasMaxLength(4000);
        builder.Property(e => e.NewValue).HasMaxLength(4000);
    }
}
