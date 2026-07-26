using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class WatchHistoryService
    {
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly MovieStore _movieStore;
        private readonly TasteProfiler _tasteProfiler;
        private readonly ILogger<WatchHistoryService> _logger;

        public WatchHistoryService(
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            MovieStore movieStore,
            TasteProfiler tasteProfiler,
            ILogger<WatchHistoryService> logger)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _movieStore = movieStore;
            _tasteProfiler = tasteProfiler;
            _logger = logger;
            
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }

        public async Task<List<MovieMetadata>> GetWatchedMoviesAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>().ToList();

            var user = _userManager.GetUserById(userId);
            if (user == null) return new List<MovieMetadata>();

            var watchedItemIds = new HashSet<Guid>();
            foreach (var movie in allMovies)
            {
                var userData = _userDataManager.GetUserData(user, movie);
                if (userData != null && userData.Played)
                {
                    watchedItemIds.Add(movie.Id);
                }
            }

            var allMetadata = await _movieStore.GetAllMoviesAsync(cancellationToken);
            return allMetadata.Where(m => watchedItemIds.Contains(m.ItemId)).ToList();
        }

        // Same as GetWatchedMoviesAsync but also returns the last-played date so the
        // taste profiler can apply time-decay. Kept separate to avoid changing the
        // signature relied on by the "Because You Watched" playlist.
        public async Task<List<(MovieMetadata Movie, DateTime? WatchedAt)>> GetWatchedMoviesWithDatesAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>().ToList();

            var user = _userManager.GetUserById(userId);
            if (user == null) return new List<(MovieMetadata, DateTime?)>();

            var watchedMeta = await _movieStore.GetAllMoviesAsync(cancellationToken);
            var watchedMetaById = watchedMeta.ToDictionary(m => m.ItemId);

            var result = new List<(MovieMetadata, DateTime?)>();
            foreach (var movie in allMovies)
            {
                var userData = _userDataManager.GetUserData(user, movie);
                if (userData != null && userData.Played && watchedMetaById.TryGetValue(movie.Id, out var meta))
                {
                    result.Add((meta, userData.LastPlayedDate));
                }
            }
            return result;
        }

        public async Task<TasteProfile> GetUserTasteProfileAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var watched = await GetWatchedMoviesWithDatesAsync(userId, cancellationToken);
            double halfLife = Plugin.Instance!.Configuration.TasteDecayHalfLifeDays;
            return _tasteProfiler.CalculateProfile(userId, watched, halfLife);
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            if (e.Item is Movie && e.UserData.Played)
            {
                _logger.LogInformation("User {UserId} watched {Movie}. Emitting watch event...", e.UserId, e.Item.Name);

                // v1.5.1: compute playback % so the engine can ignore quick glances /
                // tests (below MinCompletionPercent) — only a real watch learns.
                double? pct = null;
                if (e.Item.RunTimeTicks is > 0 && e.UserData.PlaybackPositionTicks is > 0)
                {
                    pct = (double)e.UserData.PlaybackPositionTicks / e.Item.RunTimeTicks * 100.0;
                }
                else if (e.UserData.Played)
                {
                    // Marked played without position info (e.g. user manually ticked
                    // "played" in the UI, or watched it elsewhere). Per design this IS a
                    // real watch signal: the user has seen the film, so it should count
                    // as watched for exclusion AND feed the taste profile + punish/reward.
                    // Treat as fully watched (100%) rather than a glance/test.
                    pct = 100.0;
                }

                // Fire an event that PlaylistEngine can listen to for real-time punishment + rebuild
                WatchEventEmitted?.Invoke(this, new WatchEventArgs
                {
                    UserId = e.UserId,
                    MovieId = e.Item.Id,
                    PlaybackPercentage = pct
                });
            }
        }

        public event EventHandler<WatchEventArgs>? WatchEventEmitted;
    }

    public class WatchEventArgs : EventArgs
    {
        public Guid UserId { get; set; }
        public Guid MovieId { get; set; }
        // v1.5.1: playback % at time of the watch event (null if unknown). Below
        // MinCompletionPercent => not a real watch signal (glance/test); engine ignores it.
        public double? PlaybackPercentage { get; set; }
    }
}
