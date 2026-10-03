using Microsoft.EntityFrameworkCore;
using Postarr.Models;

namespace Postarr.Data;

public class PostarrDbContext : DbContext
{
    public PostarrDbContext(DbContextOptions<PostarrDbContext> options) : base(options) { }

    public DbSet<LibraryItem>     LibraryItems   => Set<LibraryItem>();
    public DbSet<SeasonItem>      Seasons        => Set<SeasonItem>();
    public DbSet<PlexCollection>  Collections    => Set<PlexCollection>();
    public DbSet<PendingChange>   PendingChanges => Set<PendingChange>();
    public DbSet<ActivityLogEntry>ActivityLog    => Set<ActivityLogEntry>();
    public DbSet<SettingsRecord>  Settings       => Set<SettingsRecord>();
    public DbSet<ManagedCollection> ManagedCollections => Set<ManagedCollection>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<LibraryItem>().HasIndex(i => i.PlexRatingKey).IsUnique();
        m.Entity<LibraryItem>().HasMany(i => i.Seasons).WithOne()
            .HasForeignKey(s => s.LibraryItemId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<SeasonItem>().HasIndex(s => s.PlexRatingKey).IsUnique();
        m.Entity<PlexCollection>().HasIndex(c => c.PlexRatingKey).IsUnique();
        m.Entity<SettingsRecord>().HasKey(s => s.Id);
    }
}

public class SettingsRecord
{
    public int    Id          { get; set; } = 1;
    public string JsonPayload { get; set; } = "{}";
}
