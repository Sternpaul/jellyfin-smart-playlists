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

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
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
        }
    }
}
