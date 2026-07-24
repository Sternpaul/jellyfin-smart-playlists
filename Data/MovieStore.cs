using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AIRecommender.Data
{
    public class MovieStore
    {
        private readonly string _dbPath;

        public string DataDirectory => Path.GetDirectoryName(_dbPath) ?? ".";

        public MovieStore(MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths)
        {
            _dbPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "airecommender.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var db = GetContext();
            // EnsureCreated only creates tables when the database file is NEW.
            // On a pre-existing DB (e.g. one written by an older plugin version)
            // it does nothing, so any table added since that version was created
            // would be missing and queries would fail with "no such table".
            // Create each table idempotently so existing databases are upgraded
            // in place without dropping data.
            db.Database.EnsureCreated();
            EnsureTable(db, @"
                CREATE TABLE IF NOT EXISTS Movies (
                    ItemId TEXT NOT NULL,
                    Title TEXT NULL,
                    ReleaseYear INTEGER NULL,
                    ImdbId TEXT NULL,
                    Plot TEXT NULL,
                    Director TEXT NULL,
                    Cast TEXT NULL,
                    Subcategories TEXT NULL,
                    Moods TEXT NULL,
                    Themes TEXT NULL,
                    NarrativeStyle TEXT NULL,
                    Accessibility TEXT NULL,
                    Intensity TEXT NULL,
                    CriticalAcclaimScore INTEGER NOT NULL,
                    IsClassified INTEGER NOT NULL,
                    DateAdded TEXT NOT NULL,
                    LastUpdated TEXT NOT NULL,
                    Popularity REAL NOT NULL DEFAULT 0,
                    CONSTRAINT PK_Movies PRIMARY KEY (ItemId)
                )");
            EnsureTable(db, @"
                CREATE TABLE IF NOT EXISTS UserWatchlists (
                    UserId TEXT NOT NULL,
                    ImportMethod INTEGER NOT NULL,
                    JsonUrl TEXT NULL,
                    CsvData TEXT NULL,
                    EnableWatchlistPlaylist INTEGER NOT NULL,
                    LastSynced TEXT NOT NULL,
                    MatchedItemIds TEXT NULL,
                    CONSTRAINT PK_UserWatchlists PRIMARY KEY (UserId)
                )");
            EnsureTable(db, @"
                CREATE TABLE IF NOT EXISTS Affinities (
                    UserId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    Affinity REAL NOT NULL,
                    PenaltyUntil TEXT NULL,
                    LastSurfaced TEXT NULL,
                    LastUpdated TEXT NOT NULL,
                    CONSTRAINT PK_Affinities PRIMARY KEY (UserId, ItemId)
                )");
            EnsureTable(db, @"
                CREATE TABLE IF NOT EXISTS TasteSnapshots (
                    Id INTEGER NOT NULL,
                    UserId TEXT NULL,
                    SnapshotAt TEXT NOT NULL,
                    SubcategoryWeightsJson TEXT NULL,
                    MoodWeightsJson TEXT NULL,
                    CONSTRAINT PK_TasteSnapshots PRIMARY KEY (Id)
                )");
            EnsureTable(db, @"
                CREATE TABLE IF NOT EXISTS SurfaceHistory (
                    Id INTEGER NOT NULL,
                    UserId TEXT NULL,
                    ItemId TEXT NULL,
                    PlaylistType TEXT NULL,
                    SurfacedAt TEXT NOT NULL,
                    CONSTRAINT PK_SurfaceHistory PRIMARY KEY (Id)
                )");
            EnsureTable(db, @"
                CREATE TABLE IF NOT EXISTS UserRatings (
                    UserId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    Rating REAL NOT NULL,
                    SourceTitle TEXT NULL,
                    LastUpdated TEXT NOT NULL,
                    CONSTRAINT PK_UserRatings PRIMARY KEY (UserId, ItemId)
                )");
            MigrateAddMovieKeywordColumns(db);
        }

        // v1.5.14: add TmdbId + Keywords columns to the Movies table on existing
        // databases (the CREATE TABLE above only affects fresh installs). ALTER is
        // idempotent — adding a column that already exists is a no-op / caught.
        private static void MigrateAddMovieKeywordColumns(AiDbContext db)
        {
            foreach (var col in new[] { "TmdbId", "Keywords", "Popularity" })
            {
                try { db.Database.ExecuteSqlRaw($"ALTER TABLE Movies ADD COLUMN {col} TEXT NULL"); }
                catch { /* column already exists — ignore */ }
            }

            // v1.5.24: Popularity is a non-nullable double in the model but the column
            // was historically added as NULL, so every pre-v1.5.21 row has NULL there.
            // EF cannot materialize a double from NULL (GetDouble throws "data is NULL"),
            // which crashed the index task on upgraded databases. Backfill NULLs to 0
            // (the "unknown popularity → no fame penalty" sentinel). Create+drop-safe.
            try { db.Database.ExecuteSqlRaw("UPDATE Movies SET Popularity = 0 WHERE Popularity IS NULL"); }
            catch { /* column missing or already populated — ignore */ }
        }

        private static void EnsureTable(AiDbContext db, string sql)
        {
            db.Database.ExecuteSqlRaw(sql);
        }

        private AiDbContext GetContext()
        {
            return new AiDbContext(_dbPath);
        }

        public async Task<List<MovieMetadata>> GetAllMoviesAsync(CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.Movies.ToListAsync(cancellationToken);
        }

        public async Task<List<MovieMetadata>> GetUnclassifiedMoviesAsync(CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.Movies.Where(m => !m.IsClassified || m.Subcategories == "[]" || string.IsNullOrEmpty(m.Subcategories)).ToListAsync(cancellationToken);
        }

        public async Task SaveMoviesAsync(IEnumerable<MovieMetadata> movies, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            foreach (var movie in movies)
            {
                var existing = await db.Movies.FindAsync(new object[] { movie.ItemId }, cancellationToken);
                if (existing == null)
                {
                    db.Movies.Add(movie);
                }
                else
                {
                    db.Entry(existing).CurrentValues.SetValues(movie);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<UserWatchlistConfig?> GetUserWatchlistConfigAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.UserWatchlists.FindAsync(new object[] { userId }, cancellationToken);
        }

        public async Task SaveUserWatchlistConfigAsync(UserWatchlistConfig config, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            var existing = await db.UserWatchlists.FindAsync(new object[] { config.UserId }, cancellationToken);
            if (existing == null)
            {
                db.UserWatchlists.Add(config);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(config);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // ---- MovieAffinity (dynamic per-user, per-movie rating) ----

        public async Task<Dictionary<Guid, MovieAffinity>> GetAffinitiesAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            var rows = await db.Affinities
                .Where(a => a.UserId == userId.ToString())
                .ToListAsync(cancellationToken);

            var result = new Dictionary<Guid, MovieAffinity>();
            foreach (var r in rows)
                if (Guid.TryParse(r.ItemId, out var gid))
                    result[gid] = r;
            return result;
        }

        public async Task UpsertAffinitiesAsync(IEnumerable<MovieAffinity> rows, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            foreach (var row in rows)
            {
                var existing = await db.Affinities
                    .FindAsync(new object[] { row.UserId, row.ItemId }, cancellationToken);
                if (existing == null)
                {
                    db.Affinities.Add(row);
                }
                else
                {
                    existing.Affinity = row.Affinity;
                    existing.PenaltyUntil = row.PenaltyUntil;
                    existing.LastSurfaced = row.LastSurfaced;
                    existing.LastUpdated = row.LastUpdated;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Records that the given movies were just surfaced in playlists (for novelty tracking).
        public async Task MarkSurfacedAsync(Guid userId, IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        {
            var nowIso = DateTime.UtcNow.ToString("o");
            var uid = userId.ToString();
            using var db = GetContext();
            foreach (var itemId in itemIds)
            {
                var key = itemId.ToString();
                var existing = await db.Affinities
                    .FindAsync(new object[] { uid, key }, cancellationToken);
                if (existing == null)
                {
                    db.Affinities.Add(new MovieAffinity { UserId = uid, ItemId = key, LastSurfaced = nowIso });
                }
                else
                {
                    existing.LastSurfaced = nowIso;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // ---- TasteSnapshot (v1.5.4) ----

        public async Task SaveTasteSnapshotAsync(TasteSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            db.TasteSnapshots.Add(snapshot);
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<TasteSnapshot?> GetLatestTasteSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.TasteSnapshots
                .Where(t => t.UserId == userId.ToString())
                .OrderByDescending(t => t.SnapshotAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<TasteSnapshot?> GetOldestTasteSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.TasteSnapshots
                .Where(t => t.UserId == userId.ToString())
                .OrderBy(t => t.SnapshotAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // ---- SurfaceHistory (v1.5.5) ----

        public async Task RecordSurfaceHistoryAsync(Guid userId, IEnumerable<Guid> itemIds, string playlistType, CancellationToken cancellationToken = default)
        {
            var uid = userId.ToString();
            var now = DateTime.UtcNow;
            using var db = GetContext();
            foreach (var itemId in itemIds)
            {
                db.SurfaceHistory.Add(new SurfaceHistory
                {
                    UserId = uid,
                    ItemId = itemId.ToString(),
                    PlaylistType = playlistType,
                    SurfacedAt = now
                });
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<SurfaceHistory>> GetRecentSurfaceHistoryAsync(Guid userId, int limit, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.SurfaceHistory
                .Where(s => s.UserId == userId.ToString())
                .OrderByDescending(s => s.SurfacedAt)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        // ---- UserRatings (v1.5.12): per-user Letterboxd ratings matched to library ItemIds ----

        public async Task<Dictionary<Guid, double>> GetUserRatingsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            var rows = await db.UserRatings
                .Where(r => r.UserId == userId)
                .ToListAsync(cancellationToken);
            var result = new Dictionary<Guid, double>();
            foreach (var r in rows)
                result[r.ItemId] = r.Rating;
            return result;
        }

        // Replace a user's ratings wholesale (re-scraped). Old ratings for this user are cleared first.
        public async Task SaveUserRatingsAsync(Guid userId, IEnumerable<UserRating> ratings, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            var uid = userId.ToString();
            var existing = await db.UserRatings.Where(r => r.UserId == userId).ToListAsync(cancellationToken);
            if (existing.Any())
                db.UserRatings.RemoveRange(existing);
            db.UserRatings.AddRange(ratings);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
