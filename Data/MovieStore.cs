using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Data
{
    public class MovieStore
    {
        private readonly string _dbPath;
        private readonly ILogger<MovieStore> _logger;
        private readonly SemaphoreSlim _saveMoviesLock = new(1, 1);

        public string DataDirectory => Path.GetDirectoryName(_dbPath) ?? ".";

        public MovieStore(MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths, ILogger<MovieStore> logger)
        {
            _logger = logger;
            _dbPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "airecommender.db");
            _logger.LogInformation("AI Recommender DB path: {DbPath}", _dbPath);
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var db = GetContext();

            // v1.5.32: CRITICAL — flush any data stuck in a WAL sidecar file before
            // doing anything else. On Docker bind-mounts the -wal/-shm files can be
            // invisible to new connections, so data written by a prior run (Index task)
            // might exist only in the WAL and be invisible to the next reader (Refresh
            // task). Checkpoint writes that data into the main .db file. If the DB was
            // never in WAL mode (or the WAL is empty), this is a harmless no-op.
            // Then force DELETE journal mode so WAL is never used going forward.
            try
            {
                db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=DELETE");
                // Log the active journal mode so it's visible in the Jellyfin log.
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                db.Database.OpenConnection();
                cmd.CommandText = "PRAGMA journal_mode";
                var mode = cmd.ExecuteScalar()?.ToString() ?? "unknown";
                _logger.LogInformation("SQLite journal mode set to: {Mode}", mode);
            }
            catch (Exception ex)
            {
                // Brand-new DB or already in DELETE mode — safe to ignore.
                _logger.LogWarning(ex, "WAL checkpoint/journal-mode switch failed (non-fatal).");
            }

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
                    RatingsJsonUrl TEXT NULL,
                    EnableRatingsPlaylist INTEGER NOT NULL DEFAULT 0,
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
            MigrateAddUserWatchlistColumns(db);
            // v1.5.28: one-time repair for databases created before the
            // PRIMARY KEY (ItemId) constraint existed. CREATE TABLE IF NOT EXISTS
            // is a no-op on pre-existing tables, so upgraded DBs never got the PK
            // and SaveMoviesAsync (which only uses FindAsync, never a SQL upsert)
            // let duplicate ItemId rows in. Those dups crash IndexLibraryAsync
            // (ToDictionary(ItemId)) and cause inconsistent playlist matching.
            // Collapse to one row per ItemId (keep the latest) and add a UNIQUE
            // index so duplicates can never accumulate again.
            DedupMovies(db);
            EnsureUniqueMovieIndex(db);
        }

        // v1.5.25: add the v1.5.17 ratings columns to the UserWatchlists table on
        // existing databases. Without this, EF queries/inserts of EnableRatingsPlaylist
        // (and RatingsJsonUrl) throw "no such column" and the per-user watchlist/ratings
        // config page can't load or save. ALTER is idempotent (caught if column exists).
        private static void MigrateAddUserWatchlistColumns(AiDbContext db)
        {
            try { db.Database.ExecuteSqlRaw("ALTER TABLE UserWatchlists ADD COLUMN RatingsJsonUrl TEXT NULL"); }
            catch { /* column already exists — ignore */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE UserWatchlists ADD COLUMN EnableRatingsPlaylist INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists — ignore */ }
        }
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

        // v1.5.29: collapse PHANTOM duplicate rows. The previous (v1.5.28) dedup keyed
        // on ItemId, but Jellyfin re-assigns ItemId on re-add/rescan, so every phantom
        // row already had a distinct ItemId and the dedup deleted nothing — the DB kept
        // ballooning (e.g. 3367 rows for ~1700 movies, the same film stored 4x under
        // different GUIDs). The real stable identity is ImdbId. Keep one row per ImdbId
        // (latest), then one row per (Title, ReleaseYear) for rows with no ImdbId.
        // Finally normalize ItemId casing in Movies + every table that joins on ItemId,
        // so the same GUID in different casings can't split into two rows or miss joins.
        // Runs once at startup; safe to repeat (no-ops when already clean).
        private static void DedupMovies(AiDbContext db)
        {
            try
            {
                db.Database.ExecuteSqlRaw(@"
                    DELETE FROM Movies
                    WHERE ImdbId IS NOT NULL AND ImdbId <> ''
                      AND rowid NOT IN (
                        SELECT MAX(rowid) FROM Movies
                        WHERE ImdbId IS NOT NULL AND ImdbId <> ''
                        GROUP BY ImdbId
                      )");
                db.Database.ExecuteSqlRaw(@"
                    DELETE FROM Movies
                    WHERE (ImdbId IS NULL OR ImdbId = '')
                      AND rowid NOT IN (
                        SELECT MAX(rowid) FROM Movies
                        WHERE ImdbId IS NULL OR ImdbId = ''
                        GROUP BY coalesce(Title,'') || '|' || coalesce(ReleaseYear,'')
                      )");
                db.Database.ExecuteSqlRaw("UPDATE Movies SET ItemId = LOWER(ItemId)");
                db.Database.ExecuteSqlRaw("UPDATE Affinities SET ItemId = LOWER(ItemId)");
                db.Database.ExecuteSqlRaw("UPDATE SurfaceHistory SET ItemId = LOWER(ItemId)");
                db.Database.ExecuteSqlRaw("UPDATE UserRatings SET ItemId = LOWER(ItemId)");
            }
            catch { /* best-effort; ignore if schema/engine quirk */ }
        }

        // v1.5.28: guarantee one row per ItemId going forward. The PK constraint in
        // CREATE TABLE is a no-op on pre-existing tables, so add an explicit UNIQUE
        // index. Requires duplicates to already be removed (see DedupMovies).
        private static void EnsureUniqueMovieIndex(AiDbContext db)
        {
            try
            {
                db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS UX_Movies_ItemId ON Movies(ItemId)");
            }
            catch { /* index may already exist, or dups remain — both are non-fatal here */ }
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
            await _saveMoviesLock.WaitAsync(cancellationToken);
            try
            {
            // v1.5.29: dedupe in-memory by ImdbId first (a re-index pass may pass the
            // same ImdbId twice with a synced ItemId), then upsert each. Upserting by
            // ItemId alone would miss rows whose ItemId was updated to a new GUID, so we
            // also find by ImdbId and update that row's ItemId into the new value.
            using var db = GetContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var seenImdb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var movie in movies)
            {
                // Normalize the key to lowercase so lookups match the stored (lowercased)
                // value, and so we never attempt to change the primary key on update.
                // EF throws InvalidOperationException / DbUpdateConcurrencyException if a
                // key property is modified via SetValues, which crashed both the index
                // and the TMDB keyword enrichment (v1.5.34 regression: SaveMoviesAsync
                // started being called). See issue fixed in v1.5.35.
                var key = movie.ItemId.ToString().ToLowerInvariant();
                var keyGuid = Guid.Parse(key);
                MovieMetadata? existing = null;
                if (!string.IsNullOrWhiteSpace(movie.ImdbId))
                    existing = await db.Movies.FirstOrDefaultAsync(m => m.ImdbId == movie.ImdbId, cancellationToken);
                if (existing == null)
                    // Case-insensitive find: stored ItemId may be a different case (e.g. a
                    // pre-filled/backed-up DB used uppercase GUIDs) and FindAsync is
                    // case-sensitive in SQLite, which caused "0 rows affected" crashes
                    // (DbUpdateConcurrencyException) on the otherwise-correct update path.
                    existing = await db.Movies.FirstOrDefaultAsync(m => m.ItemId.ToString().ToLower() == key, cancellationToken);
                if (existing == null)
                {
                    movie.ItemId = keyGuid; // normalize on insert
                    db.Movies.Add(movie);
                }
                else if (existing.ItemId == keyGuid)
                {
                    // Same key: canonicalize the stored Guid text first. A legacy
                    // lowercase key is found by the case-insensitive lookup above, but
                    // EF's tracked UPDATE uses its canonical Guid text in the WHERE
                    // clause and otherwise reports a false zero-row concurrency error.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Movies SET ItemId = {existing.ItemId} WHERE LOWER(ItemId) = {key}",
                        cancellationToken);
                    movie.ItemId = existing.ItemId;
                    db.Entry(existing).CurrentValues.SetValues(movie);
                }
                else
                {
                    // v1.5.40: the row was found by ImdbId but Jellyfin re-assigned the
                    // movie a NEW ItemId (re-added/rescanned item). ItemId is the EF
                    // PRIMARY KEY and EF forbids modifying a tracked entity's key
                    // ("property 'MovieMetadata.ItemId' is part of a key and so cannot
                    // be modified") — the old in-place `existing.ItemId = keyGuid`
                    // crashed the whole index task here. Correct EF pattern: delete the
                    // old row, flush, then insert a fresh row under the new key. Keep
                    // the sequence transactional and migrate dependent references.
                    await MigrateItemReferencesAsync(db, existing.ItemId, keyGuid, cancellationToken);
                    // Detach before deleting with case-insensitive SQL. Legacy databases
                    // can store lowercase GUID text while EF's Guid converter emits a
                    // differently-cased key in DELETE, producing a false 0-row
                    // concurrency failure.
                    db.Entry(existing).State = EntityState.Detached;
                    var staleId = existing.ItemId.ToString().ToLowerInvariant();
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"DELETE FROM Movies WHERE LOWER(ItemId) = {staleId}",
                        cancellationToken);
                    // Guard: another row may already sit under the new key (duplicate
                    // from an older index run). Update it in place instead of inserting
                    // a conflicting PK.
                    var occupant = await db.Movies.FirstOrDefaultAsync(m => m.ItemId == keyGuid, cancellationToken);
                    movie.ItemId = keyGuid;
                    if (occupant != null)
                        db.Entry(occupant).CurrentValues.SetValues(movie);
                    else
                        db.Movies.Add(movie);
                }
                if (!string.IsNullOrWhiteSpace(movie.ImdbId)) seenImdb.Add(movie.ImdbId);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // v1.5.32: confirm persistence — log the actual row count after commit.
            var totalRows = await db.Movies.CountAsync(cancellationToken);
            _logger.LogInformation("SaveMoviesAsync committed. DB now has {Total} movie rows.", totalRows);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var entries = string.Join("; ", ex.Entries.Select(entry =>
                    $"{entry.Metadata.ClrType.Name}:{entry.State}:" +
                    string.Join(",", entry.Properties
                        .Where(property => property.Metadata.IsPrimaryKey())
                        .Select(property => $"{property.Metadata.Name}={property.CurrentValue}"))));
                _logger.LogError(ex, "SaveMoviesAsync concurrency failure entries: {Entries}", entries);
                throw;
            }
            finally
            {
                _saveMoviesLock.Release();
            }
        }

        private static async Task MigrateItemReferencesAsync(
            AiDbContext db,
            Guid oldItemId,
            Guid newItemId,
            CancellationToken cancellationToken)
        {
            var oldId = oldItemId.ToString().ToLowerInvariant();
            var newId = newItemId.ToString().ToLowerInvariant();

            // Composite-key rows cannot have ItemId modified while tracked. Copy them
            // under the replacement key with SQLite conflict handling, then remove the
            // stale-key rows. This also repairs remnants of a partial older migration.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT OR REPLACE INTO Affinities
                    (UserId, ItemId, Affinity, PenaltyUntil, LastSurfaced, LastUpdated)
                SELECT UserId, {newId}, Affinity, PenaltyUntil, LastSurfaced, LastUpdated
                FROM Affinities WHERE LOWER(ItemId) = {oldId}", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM Affinities WHERE LOWER(ItemId) = {oldId}", cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT OR REPLACE INTO UserRatings
                    (UserId, ItemId, Rating, SourceTitle, LastUpdated)
                SELECT UserId, {newId}, Rating, SourceTitle, LastUpdated
                FROM UserRatings WHERE LOWER(ItemId) = {oldId}", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM UserRatings WHERE LOWER(ItemId) = {oldId}", cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE SurfaceHistory SET ItemId = {newId} WHERE LOWER(ItemId) = {oldId}",
                cancellationToken);

            // Cached watchlist matches are JSON. Update the text directly instead of
            // tracking UserWatchlistConfig: legacy databases can store UserId keys in
            // undashed form, and EF's dashed Guid UPDATE then affects zero rows.
            var oldCompact = oldItemId.ToString("N");
            var newCompact = newItemId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE UserWatchlists
                SET MatchedItemIds = REPLACE(
                    REPLACE(
                        REPLACE(
                            REPLACE(MatchedItemIds, {oldId}, {newId}),
                            {oldId.ToUpperInvariant()}, {newId}),
                        {oldCompact}, {newCompact}),
                    {oldCompact.ToUpperInvariant()}, {newCompact})
                WHERE MatchedItemIds IS NOT NULL", cancellationToken);
        }

        // v1.5.28: prune rows whose ItemId is no longer present in the Jellyfin
        // library. The index only ever added rows (OnItemRemoved was a no-op), so
        // deleted movies accumulated as orphans and the DB grew far larger than the
        // real library — which also inflated TMDB enrichment counts and playlist
        // matching. Pass the set of current Jellyfin movie ItemIds; anything in the
        // DB not in that set is deleted.
        public async Task<int> DeleteMoviesNotInAsync(HashSet<Guid> liveItemIds, CancellationToken cancellationToken = default)
        {
            // v1.5.29: raw SQL delete (bypasses EF change-tracking/concurrency checks).
            // The previous EF RemoveRange threw DbUpdateConcurrencyException against
            // some DBs; raw SQL is reliable and fast.
            using var db = GetContext();
            // Collect the orphan ItemIds first (parameter lists are awkward in EF Core
            // raw SQL, so do the membership test in SQL via a temp set of NOT IN).
            var orphans = await db.Movies
                .Where(m => !liveItemIds.Contains(m.ItemId))
                .Select(m => m.ItemId)
                .ToListAsync(cancellationToken);
            if (orphans.Count == 0) return 0;
            var idList = string.Join(",", orphans.Select(g => $"'{g.ToString().ToLowerInvariant()}'"));
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM Movies WHERE LOWER(ItemId) IN ({idList})");
            return orphans.Count;
        }

        public async Task<UserWatchlistConfig?> GetUserWatchlistConfigAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.UserWatchlists.FindAsync(new object[] { userId }, cancellationToken);
        }

        public async Task SaveUserWatchlistConfigAsync(UserWatchlistConfig config, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            var existing = await db.UserWatchlists.FirstOrDefaultAsync(w => w.UserId == config.UserId, cancellationToken);
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
                    .FirstOrDefaultAsync(a => a.UserId == row.UserId && a.ItemId == row.ItemId, cancellationToken);
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
            // Raw delete (bypasses EF change-tracking/concurrency checks) then insert.
            // The previous RemoveRange + SaveChangesAsync threw DbUpdateConcurrencyException
            // ("expected 1 row, affected 0") against case-mismatched / already-removed rows.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM UserRatings WHERE UserId = {0}", userId);
            db.UserRatings.AddRange(ratings);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
