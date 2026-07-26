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
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
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
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<WatchHistoryService> _logger;

        public WatchHistoryService(
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            MovieStore movieStore,
            TasteProfiler tasteProfiler,
            ISessionManager sessionManager,
            ILogger<WatchHistoryService> logger)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _movieStore = movieStore;
            _tasteProfiler = tasteProfiler;
            _sessionManager = sessionManager;
            _logger = logger;

            _sessionManager.PlaybackStopped += OnPlaybackStopped;
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

        // Recency and taste use only playback sessions the plugin actually observed.
        // Jellyfin's Played flag remains the separate, broader exclusion source above.
        public async Task<List<(MovieMetadata Movie, DateTime? WatchedAt)>> GetWatchedMoviesWithDatesAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var verifiedDates = await _movieStore.GetVerifiedWatchDatesAsync(userId, cancellationToken);
            var metadata = await _movieStore.GetAllMoviesAsync(cancellationToken);
            return metadata
                .Where(movie => verifiedDates.ContainsKey(movie.ItemId))
                .Select(movie => (movie, (DateTime?)verifiedDates[movie.ItemId]))
                .ToList();
        }

        public async Task<TasteProfile> GetUserTasteProfileAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var watched = await GetWatchedMoviesWithDatesAsync(userId, cancellationToken);
            double halfLife = Plugin.Instance!.Configuration.TasteDecayHalfLifeDays;
            return _tasteProfiler.CalculateProfile(userId, watched, halfLife);
        }

        private static double? GetVerifiedPlaybackPercentage(
            UserDataSaveReason saveReason,
            bool played,
            long playbackPositionTicks,
            long runtimeTicks)
        {
            // Only a finished Jellyfin playback session is evidence of when a movie
            // was watched. TogglePlayed merely means the user has seen it sometime.
            if (saveReason != UserDataSaveReason.PlaybackFinished)
                return null;

            if (runtimeTicks > 0 && playbackPositionTicks > 0)
            {
                var percentage = (double)playbackPositionTicks / runtimeTicks * 100.0;
                return percentage > 50.0 ? percentage : null;
            }

            // Played=true with a reset/unknown position is still not direct evidence
            // of >50%; the session stop event normally retains the original position.
            return null;
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            if (e.Item is not Movie)
                return;

            var percentage = GetVerifiedPlaybackPercentage(
                UserDataSaveReason.PlaybackFinished,
                false,
                e.PlaybackPositionTicks ?? 0,
                e.Item.RunTimeTicks ?? 0);
            if (!percentage.HasValue)
                return;

            foreach (var user in e.Users)
            {
                try
                {
                    var watchedAt = DateTime.UtcNow;
                    await _movieStore.RecordVerifiedWatchAsync(
                        user.Id,
                        e.Item.Id,
                        watchedAt,
                        percentage.Value,
                        CancellationToken.None);
                    _logger.LogInformation(
                        "Verified Jellyfin playback of {Movie} by {UserId} at {Percentage:F1}%; recording recency and emitting watch event.",
                        e.Item.Name,
                        user.Id,
                        percentage.Value);

                    WatchEventEmitted?.Invoke(this, new WatchEventArgs
                    {
                        UserId = user.Id,
                        MovieId = e.Item.Id,
                        PlaybackPercentage = percentage.Value
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to record verified playback for {MovieId} by {UserId}.", e.Item.Id, user.Id);
                }
            }
        }

        public event EventHandler<WatchEventArgs>? WatchEventEmitted;
    }

    public class WatchEventArgs : EventArgs
    {
        public Guid UserId { get; set; }
        public Guid MovieId { get; set; }
        // Actual playback percentage from a verified playback stop strictly above 50%.
        public double? PlaybackPercentage { get; set; }
    }
}
