using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AIRecommender.Data
{
    public class AiDbContext : DbContext
    {
        private readonly string _databasePath;

        public AiDbContext(string databasePath)
        {
            _databasePath = databasePath;
        }

        public DbSet<MovieMetadata> Movies { get; set; }
        public DbSet<UserWatchlistConfig> UserWatchlists { get; set; }
        public DbSet<MovieAffinity> Affinities { get; set; }
        public DbSet<TasteSnapshot> TasteSnapshots { get; set; }
        public DbSet<SurfaceHistory> SurfaceHistory { get; set; }
        public DbSet<UserRating> UserRatings { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // v1.5.32: force DELETE journal mode. EF Core's SQLite provider defaults
            // to WAL (Write-Ahead Logging), which writes to sidecar files (-wal/-shm).
            // On Docker bind-mounts these sidecar files can be invisible to new
            // connections, causing readers to see an empty table while the data sits in
            // the WAL file. DELETE mode uses a traditional rollback journal that writes
            // directly to the main .db file — every connection sees committed data.
            optionsBuilder.UseSqlite($"Data Source={_databasePath};Journal Mode=Delete");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MovieMetadata>()
                .HasKey(m => m.ItemId);

            modelBuilder.Entity<UserWatchlistConfig>()
                .HasKey(u => u.UserId);

            modelBuilder.Entity<MovieAffinity>()
                .HasKey(a => new { a.UserId, a.ItemId });

            modelBuilder.Entity<TasteSnapshot>()
                .HasKey(t => t.Id);

            modelBuilder.Entity<SurfaceHistory>()
                .HasKey(s => s.Id);

            modelBuilder.Entity<UserRating>()
                .HasKey(r => new { r.UserId, r.ItemId });
        }
    }
}
