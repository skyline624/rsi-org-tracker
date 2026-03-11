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

    public TrackerDbContext(DbContextOptions<TrackerDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TrackerDbContext).Assembly);
    }
}