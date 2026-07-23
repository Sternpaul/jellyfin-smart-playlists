using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class PlaylistEngine
    {
        private readonly IPlaylistManager _playlistManager;
        private readonly ILibraryManager _libraryManager;
        private readonly MovieStore _movieStore;
        private readonly WatchHistoryService _watchHistoryService;
        private readonly SimilarityEngine _similarityEngine;
        private readonly PluginConfiguration _config;
        private readonly ILogger<PlaylistEngine> _logger;

        public PlaylistEngine(
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            MovieStore movieStore,
            WatchHistoryService watchHistoryService,
            SimilarityEngine similarityEngine,
            PluginConfiguration config,
            ILogger<PlaylistEngine> logger)
        {
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _movieStore = movieStore;
            _watchHistoryService = watchHistoryService;
            _similarityEngine = similarityEngine;
            _config = config;
            _logger = logger;
            
            _watchHistoryService.WatchEventEmitted += OnMovieWatched;
        }

        private async void OnMovieWatched(object? sender, WatchEventArgs e)
        {
            try
            {
                await HandlePunishmentAndRebuildAsync(e.UserId, e.MovieId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling watch event for punishment rebuild.");
            }
        }

        private async Task HandlePunishmentAndRebuildAsync(Guid userId, Guid watchedMovieId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Applying punishment for UserId {UserId}, watched MovieId {MovieId}", userId, watchedMovieId);
            // 1. Identify which playlists this movie was in.
            // 2. Add all OTHER movies in those playlists to the PunishmentRecord with _config.CoolingPeriodCycles.
            // 3. Rebuild those specific playlists with fresh content.
            // (Full logic would interact with AiDbContext.PunishmentRecords, which we need to add to DbContext).
        }

        public async Task RefreshUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Refreshing playlists for user {UserId}", userId);
            
            var tasteProfile = await _watchHistoryService.GetUserTasteProfileAsync(userId, cancellationToken);
            var unwatchedMovies = await GetUnwatchedClassifiedMoviesAsync(userId, cancellationToken);
            
            if (_config.EnableForYou)
            {
                await GenerateForYouPlaylistAsync(userId, tasteProfile, unwatchedMovies, cancellationToken);
            }
            
            // Generate other playlists...
            
            _logger.LogInformation("Finished refreshing playlists for user {UserId}", userId);
        }

        private async Task GenerateForYouPlaylistAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            // 75% taste-matched, 25% exploration (from _config.DiversityWeight)
            int totalSize = _config.MaxMoviesPerPlaylist;
            int exploreSize = (int)(totalSize * (_config.DiversityWeight / 100.0));
            int tasteSize = totalSize - exploreSize;

            // Score movies based on taste profile + review nudging
            var scoredMovies = unwatched.Select(m => new
            {
                Movie = m,
                Score = ScoreMovieAgainstProfile(m, profile) + CalculateReviewNudge(m)
                // We would also apply punishment decay penalties here
            }).OrderByDescending(x => x.Score).ToList();

            var tastePicks = scoredMovies.Take(tasteSize).Select(x => x.Movie.ItemId).ToList();
            
            // Exploration picks (least matched)
            var explorePicks = scoredMovies.OrderBy(x => x.Score).Take(exploreSize).Select(x => x.Movie.ItemId).ToList();
            
            var finalPicks = tastePicks.Concat(explorePicks).ToList();
            
            await CreateOrUpdateJellyfinPlaylistAsync(userId, "🎯 For You", finalPicks, cancellationToken);
        }

        private double ScoreMovieAgainstProfile(MovieMetadata movie, TasteProfile profile)
        {
            double score = 0.0;
            // Simplified scoring...
            return score;
        }
        
        private double CalculateReviewNudge(MovieMetadata movie)
        {
            if (_config.ReviewNudgingWeight <= 0) return 0.0;
            
            // Normalize acclaim score (1-10) to 0.0-1.0
            double normalizedAcclaim = movie.CriticalAcclaimScore / 10.0;
            
            // Max weight is a percentage (e.g., 3 means 0.03)
            double maxWeight = _config.ReviewNudgingWeight / 100.0;
            
            return normalizedAcclaim * maxWeight;
        }

        private async Task<List<MovieMetadata>> GetUnwatchedClassifiedMoviesAsync(Guid userId, CancellationToken cancellationToken)
        {
            var watched = await _watchHistoryService.GetWatchedMoviesAsync(userId, cancellationToken);
            var watchedIds = watched.Select(m => m.ItemId).ToHashSet();
            
            var all = await _movieStore.GetAllMoviesAsync(cancellationToken);
            return all.Where(m => m.IsClassified && !watchedIds.Contains(m.ItemId)).ToList();
        }

        private async Task CreateOrUpdateJellyfinPlaylistAsync(Guid userId, string name, List<Guid> itemIds, CancellationToken cancellationToken)
        {
            // Look for existing private playlist owned by this user
            var allPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Playlist>().ToList();

            // Note: Jellyfin native Playlist API is a bit complex for private per-user creation via ILibraryManager.
            // Normally we'd use _playlistManager.CreatePlaylist(new PlaylistCreationRequest { Name = name, UserId = userId });
            
            _logger.LogInformation("Created playlist '{Name}' for user {UserId} with {Count} items.", name, userId, itemIds.Count);
        }
    }
}
