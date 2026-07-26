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
        public DbSet<VerifiedWatch> VerifiedWatches { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // NOTE: do NOT set the journal mode here. Microsoft.Data.Sqlite does NOT
            // support "Journal Mode=Delete" (or any journal-mode keyword) in the
            // connection string — it throws System.ArgumentException at open time
            // ("Connection string keyword 'journal mode' is not supported"), which
            // crashes MovieStore construction and both scheduled tasks.
            // Journal mode is switched to DELETE via "PRAGMA journal_mode=DELETE" in
            // MovieStore.InitializeDatabase(). The connection string stays minimal.
            optionsBuilder.UseSqlite($"Data Source={_databasePath}");
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

            modelBuilder.Entity<VerifiedWatch>()
                .HasKey(w => new { w.UserId, w.ItemId });
        }
    }
}
