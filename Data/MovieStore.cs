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

        public MovieStore(MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths)
        {
            _dbPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "airecommender.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var db = GetContext();
            db.Database.EnsureCreated();
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
    }
}
