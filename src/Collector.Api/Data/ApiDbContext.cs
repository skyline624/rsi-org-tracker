using Collector.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Data;

public class ApiDbContext : DbContext
{
    public DbSet<ApiUser> ApiUsers { get; set; } = null!;
    public DbSet<ApiKey> ApiKeys { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
    public DbSet<ActivityLog> ActivityLogs { get; set; } = null!;

    public ApiDbContext(DbContextOptions<ApiDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiUser>(e =>
        {
            e.ToTable("api_users");
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Username).IsRequired().HasMaxLength(100);
            e.Property(u => u.Email).IsRequired().HasMaxLength(255);
            e.Property(u => u.PasswordHash).IsRequired();
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.Property(k => k.Name).IsRequired().HasMaxLength(100);
            e.Property(k => k.KeyHash).IsRequired();
            e.Property(k => k.KeyPrefix).IsRequired().HasMaxLength(16);
            e.HasOne(k => k.ApiUser)
                .WithMany(u => u.ApiKeys)
                .HasForeignKey(k => k.ApiUserId);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.Property(t => t.TokenHash).IsRequired();
            e.HasOne(t => t.ApiUser)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.ApiUserId);
        });

        modelBuilder.Entity<ActivityLog>(e =>
        {
            e.ToTable("activity_logs");
            e.HasKey(l => l.Id);
            e.Property(l => l.Action).IsRequired().HasMaxLength(200);
            e.Property(l => l.EntityType).HasMaxLength(100);
            e.Property(l => l.IpAddress).HasMaxLength(45);
            e.HasOne(l => l.ApiUser)
                .WithMany()
                .HasForeignKey(l => l.ApiUserId)
                .IsRequired(false);
        });
    }
}
