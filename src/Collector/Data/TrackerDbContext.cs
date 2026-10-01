using Microsoft.EntityFrameworkCore;
using Collector.Models;

namespace Collector.Data;

/// <summary>
/// Entity Framework Core database context for the tracker.
/// </summary>
public class TrackerDbContext : DbContext
{
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<OrganizationMember> OrganizationMembers { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<UserHandleHistory> UserHandleHistories { get; set; } = null!;
    public DbSet<UserEnrichmentQueue> UserEnrichmentQueue { get; set; } = null!;
    public DbSet<MemberCollectionLog> MemberCollectionLogs { get; set; } = null!;
    public DbSet<ChangeEvent> ChangeEvents { get; set; } = null!;
    public DbSet<DiscoveredOrganization> DiscoveredOrganizations { get; set; } = null!;
    public DbSet<TrackedEntity> TrackedEntities { get; set; } = null!;
    public DbSet<EntityNote> EntityNotes { get; set; } = null!;
    public DbSet<EntityAudio> EntityAudios { get; set; } = null!;
    public DbSet<EntityMembership> EntityMemberships { get; set; } = null!;
    public DbSet<OrgNote> OrgNotes { get; set; } = null!;
    public DbSet<EntityLink> EntityLinks { get; set; } = null!;
    public DbSet<OrgMemberCount> OrgMemberCounts { get; set; } = null!;
    public DbSet<DiscordGuild> DiscordGuilds { get; set; } = null!;
    public DbSet<DiscordRole> DiscordRoles { get; set; } = null!;
    public DbSet<DiscordAccount> DiscordAccounts { get; set; } = null!;
    public DbSet<DiscordMember> DiscordMembers { get; set; } = null!;
    public DbSet<DiscordMemberEvent> DiscordMemberEvents { get; set; } = null!;
    public DbSet<DiscordSync> DiscordSyncs { get; set; } = null!;
    public DbSet<DiscordOptOut> DiscordOptOuts { get; set; } = null!;
    public DbSet<DiscordGuildOptOut> DiscordGuildOptOuts { get; set; } = null!;
    public DbSet<DiscordLinkRejection> DiscordLinkRejections { get; set; } = null!;

    public TrackerDbContext(DbContextOptions<TrackerDbContext> options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TrackerDbContext).Assembly);
    }
}
