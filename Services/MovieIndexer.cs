using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class MovieIndexer
    {
        private readonly ILibraryManager _libraryManager;
        private readonly MovieStore _movieStore;
        private readonly ILogger<MovieIndexer> _logger;
        private readonly MovieClassifier _movieClassifier;

        public MovieIndexer(
            ILibraryManager libraryManager,
            MovieStore movieStore,
            ILogger<MovieIndexer> logger,
            MovieClassifier movieClassifier)
        {
            _libraryManager = libraryManager;
            _movieStore = movieStore;
            _logger = logger;
            _movieClassifier = movieClassifier;
            
            // Hook into library events for incremental indexing
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
        }

        public async Task IndexLibraryAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting full library index for AI Recommender...");
            
            var newOrUpdatedMovies = new List<MovieMetadata>();

            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>().ToList();

            var existingMovies = await _movieStore.GetAllMoviesAsync(cancellationToken);
            // v1.5.29: Jellyfin re-assigns ItemId on re-add/rescan and can store the
            // same GUID in different casings, so ItemId is NOT a stable identity. Build
            // the lookup on ImdbId (the real stable key) and also track ItemId, since
            // the row's ItemId is what we must keep in sync when Jellyfin moves a movie
            // to a new GUID. Normalize casing so "C2DA..." and "c2da..." match.
            var byImdb = existingMovies
                .Where(m => !string.IsNullOrWhiteSpace(m.ImdbId))
                .GroupBy(m => m.ImdbId!.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.LastUpdated).First());
            var byTitleYear = existingMovies
                .Where(m => string.IsNullOrWhiteSpace(m.ImdbId))
                .GroupBy(m => $"{m.Title}|{m.ReleaseYear}")
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.LastUpdated).First());

            _logger.LogInformation("Library scan: {LibraryCount} movies in Jellyfin, {DbCount} in recommender DB.", allMovies.Count, existingMovies.Count);

            foreach (var movie in allMovies)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Always normalize the ItemId casing so future joins never split.
                var itemId = movie.Id;
                movie.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Imdb.ToString(), out var imdbId);
                imdbId = string.IsNullOrWhiteSpace(imdbId) ? null : imdbId.Trim();

                // Reuse the existing row by stable identity: if Jellyfin re-assigned the
                // movie a new GUID, update the row's ItemId to the new one instead of
                // inserting a duplicate. This is what stopped the DB from ballooning.
                MovieMetadata? metadata = null;
                if (imdbId != null && byImdb.TryGetValue(imdbId.ToLowerInvariant(), out var byImdbMeta))
                    metadata = byImdbMeta;
                else if (imdbId == null)
                    byTitleYear.TryGetValue($"{movie.Name}|{movie.ProductionYear}", out metadata);

                if (metadata == null)
                {
                    metadata = new MovieMetadata
                    {
                        ItemId = itemId,
                        DateAdded = DateTime.UtcNow
                    };
                }
                else
                {
                    // Keep the row, but sync its ItemId to the current Jellyfin GUID
                    // (in case it changed) so Affinity/SurfaceHistory joins still line up.
                    metadata.ItemId = itemId;
                }

                // Always sync basic metadata in case it changed in Jellyfin
                UpdateMetadataFromJellyfinItem(movie, metadata);
            }

            if (newOrUpdatedMovies.Any())
            {
                await _movieStore.SaveMoviesAsync(newOrUpdatedMovies, cancellationToken);
                _logger.LogInformation("Indexed {Count} new/updated movies.", newOrUpdatedMovies.Count);
            }

            // v1.5.28: prune orphan rows for movies that no longer exist in Jellyfin
            // (the index only ever added before, so deleted movies accumulated and
            // the DB grew far larger than the real library — e.g. 3328 rows for a
            // smaller library). Deletes rows whose ItemId isn't in this scan.
            var liveIds = new HashSet<Guid>(allMovies.Select(m => m.Id));
            try
            {
                var removed = await _movieStore.DeleteMoviesNotInAsync(liveIds, cancellationToken);
                if (removed > 0)
                    _logger.LogInformation("Pruned {Count} orphaned movie rows no longer in the library.", removed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orphan pruning failed; continuing.");
            }

            _logger.LogInformation("Library indexing complete.");
        }

        private void UpdateMetadataFromJellyfinItem(Movie jellyfinMovie, MovieMetadata metadata)
        {
            metadata.Title = jellyfinMovie.Name;
            metadata.ReleaseYear = jellyfinMovie.ProductionYear;
            metadata.Plot = jellyfinMovie.Overview;
            
            // Try to extract IMDB ID
            jellyfinMovie.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId);
            metadata.ImdbId = imdbId;

            // v1.5.14: capture TMDB id too (needed to fetch TMDB keywords)
            jellyfinMovie.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId);
            if (!string.IsNullOrWhiteSpace(tmdbId) && int.TryParse(tmdbId, out var parsedTmdb))
                metadata.TmdbId = parsedTmdb;

            // Extract director and top-billed cast from the Jellyfin item's People.
            // In Jellyfin 10.11, People is read via ILibraryManager.GetPeople(BaseItem)
            // (returns IReadOnlyList<PersonInfo>); PersonInfo.Type is the PersonKind enum.
            // The SimilarityEngine splits these on ',' so we join multiple with commas.
            var directors = new List<string>();
            var cast = new List<string>();
            var people = _libraryManager.GetPeople(jellyfinMovie);
            if (people != null)
            {
                foreach (var person in people)
                {
                    if (person == null) continue;
                    if (person.Type == PersonKind.Director && !string.IsNullOrWhiteSpace(person.Name))
                        directors.Add(person.Name.Trim());
                    else if (person.Type == PersonKind.Actor && !string.IsNullOrWhiteSpace(person.Name))
                        cast.Add(person.Name.Trim());
                }
            }
            // Cap cast to the top 12 billed names to keep the field compact.
            metadata.Director = directors.Count > 0 ? string.Join(", ", directors) : string.Empty;
            metadata.Cast = cast.Count > 0 ? string.Join(", ", cast.Take(12)) : string.Empty;

            metadata.LastUpdated = DateTime.UtcNow;
            
            // Note: We don't reset IsClassified to false unless the Plot significantly changes.
            // For now, if it's already classified, it remains classified.
        }

        // v1.5.25: when a movie is incrementally added, classify it soon (debounced) instead
        // of waiting for the next daily Index&Classify run. Without this, newly-added
        // movies are indexed (metadata saved) but never classified until 2am.
        private System.Timers.Timer? _classifyTimer;
        private readonly object _classifyTimerLock = new();

        private void ScheduleClassify()
        {
            lock (_classifyTimerLock)
            {
                if (_classifyTimer == null)
                {
                    _classifyTimer = new System.Timers.Timer(TimeSpan.FromSeconds(20).TotalMilliseconds)
                    {
                        AutoReset = false
                    };
                    _classifyTimer.Elapsed += async (s, e) =>
                    {
                        // v1.5.26: retry up to 3 times so a transient AI/rate-limit error
                        // doesn't leave newly-added movies permanently unclassified.
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            try
                            {
                                await _movieClassifier.ClassifyPendingMoviesAsync(CancellationToken.None);
                                return;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Debounced classification after add failed (attempt {Attempt}/3).", attempt);
                                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(30));
                            }
                        }
                    };
                }
                _classifyTimer.Stop();   // reset the window on each new add
                _classifyTimer.Start();
            }
        }

        // --- Incremental Event Handlers ---

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie movie)
            {
                _logger.LogInformation("New movie detected: {Title}. Indexing for AI...", movie.Name);
                var metadata = new MovieMetadata { ItemId = movie.Id, DateAdded = DateTime.UtcNow, IsClassified = false };
                UpdateMetadataFromJellyfinItem(movie, metadata);

                // Fire and forget save
                Task.Run(() => _movieStore.SaveMoviesAsync(new[] { metadata }));
                ScheduleClassify();
            }
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie movie)
            {
                Task.Run(async () =>
                {
                    var existing = (await _movieStore.GetAllMoviesAsync()).FirstOrDefault(m => m.ItemId == movie.Id);
                    if (existing != null)
                    {
                        UpdateMetadataFromJellyfinItem(movie, existing);
                        await _movieStore.SaveMoviesAsync(new[] { existing });
                    }
                });
            }
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie movie)
            {
                // In a production scenario we might delete it from the SQLite DB,
                // but for now we can leave it (or implement a delete method in MovieStore).
            }
        }
    }
}
