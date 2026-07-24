using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
        private readonly LetterboxdService _letterboxdService;
        private PluginConfiguration _config => Plugin.Instance!.Configuration;
        private readonly ILogger<PlaylistEngine> _logger;

        public PlaylistEngine(
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            MovieStore movieStore,
            WatchHistoryService watchHistoryService,
            SimilarityEngine similarityEngine,
            LetterboxdService letterboxdService,
            ILogger<PlaylistEngine> logger)
        {
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _movieStore = movieStore;
            _watchHistoryService = watchHistoryService;
            _similarityEngine = similarityEngine;
            _letterboxdService = letterboxdService;
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
            
            // For MVP: Since watched movies are naturally filtered out by GetUnwatchedClassifiedMoviesAsync(),
            // a simple playlist refresh will achieve 90% of the value by removing the watched item and rotating the playlist.
            // A full DB-backed punishment matrix for collateral damage (cooling period for siblings) will be added in a future phase.
            
            await RefreshUserPlaylistsAsync(userId, cancellationToken);
        }

        public async Task RefreshUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            // Respect per-user exclusions configured by the admin.
            if (_config.DisabledUserIds != null &&
                _config.DisabledUserIds.Any(id => string.Equals(id, userId.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("User {UserId} is disabled; removing their recommendation playlists.", userId);
                await DeleteUserRecommendationPlaylistsAsync(userId, cancellationToken);
                return;
            }

            // Clean slate: remove any existing recommendation playlists for this user
            // before regenerating, so stale/disabled/renamed ones never linger.
            await DeleteUserRecommendationPlaylistsAsync(userId, cancellationToken);

            _logger.LogInformation("Refreshing playlists for user {UserId}", userId);
            
            var tasteProfile = await _watchHistoryService.GetUserTasteProfileAsync(userId, cancellationToken);
            var unwatchedMovies = await GetUnwatchedClassifiedMoviesAsync(userId, cancellationToken);
            
            if (_config.EnableForYou)
                await GenerateForYouPlaylistAsync(userId, tasteProfile, unwatchedMovies, cancellationToken);

            if (_config.EnableBecauseYouWatched)
                await GenerateBecauseYouWatchedPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableHiddenGems)
                await GenerateHiddenGemsPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableRecentlyAdded)
                await GenerateRecentlyAddedPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableSubcategory || _config.EnableDiscover)
                await GenerateSubcategoryPlaylistsAsync(userId, tasteProfile, unwatchedMovies, cancellationToken);
                
            if (_config.EnableWildCard)
                await GenerateWildCardPlaylistAsync(userId, unwatchedMovies, cancellationToken);

            // Watchlist is handled separately if the user enabled it
            var userConfig = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (userConfig != null && userConfig.EnableWatchlistPlaylist)
            {
                await GenerateWatchlistPlaylistAsync(userId, unwatchedMovies, cancellationToken);
            }
            
            _logger.LogInformation("Finished refreshing playlists for user {UserId}", userId);
        }

        // Deletes only the recommendation playlists this plugin created for a user,
        // identified by their known name patterns. User-created playlists are left alone.
        private async Task DeleteUserRecommendationPlaylistsAsync(Guid userId, CancellationToken cancellationToken)
        {
            var allPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Playlist>().ToList();

            foreach (var playlist in allPlaylists.Where(p => p.OwnerUserId == userId && IsRecommendationPlaylistName(p.Name)))
            {
                _logger.LogInformation("Deleting recommendation playlist '{Name}' for disabled user {UserId}.", playlist.Name, userId);
                _libraryManager.DeleteItem(playlist, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true });
            }

            await Task.CompletedTask;
        }

        private static bool IsRecommendationPlaylistName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name == "For You"
                || name.StartsWith("Because You Watched", StringComparison.OrdinalIgnoreCase)
                || name == "Hidden Gems"
                || name == "Recently Added"
                || name == "Discover: Hidden World"
                || name == "Wild Card"
                || name == "From Your Watchlist"
                || name.EndsWith("For You", StringComparison.OrdinalIgnoreCase);
        }

        private async Task GenerateForYouPlaylistAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            // 75% taste-matched, 25% exploration (from _config.DiversityWeight)
            int totalSize = _config.MaxMoviesPerPlaylist;
            int exploreSize = (int)(totalSize * (_config.DiversityWeight / 100.0));
            int tasteSize = totalSize - exploreSize;

            // Score movies based on taste profile + review nudging.
            // If the user has no watch history yet, the taste profile is empty, so
            // fall back to critical acclaim so "For You" still surfaces quality picks.
            bool hasTaste = profile.SubcategoryPreferences.Any() || profile.MoodPreferences.Any();
            var scoredMovies = unwatched.Select(m => new
            {
                Movie = m,
                Score = (hasTaste ? ScoreMovieAgainstProfile(m, profile) : 0.0)
                        + CalculateReviewNudge(m)
                        + (hasTaste ? 0.0 : m.CriticalAcclaimScore / 10.0)
            }).OrderByDescending(x => x.Score).ToList();

            var tastePicks = scoredMovies.Take(tasteSize).Select(x => x.Movie.ItemId).ToList();
            
            // Exploration picks (least matched)
            var explorePicks = scoredMovies.OrderBy(x => x.Score).Take(exploreSize).Select(x => x.Movie.ItemId).ToList();
            
            var finalPicks = tastePicks.Concat(explorePicks).ToList();
            
            await CreateOrUpdateJellyfinPlaylistAsync(userId, "For You", finalPicks, cancellationToken);
        }

        private async Task GenerateBecauseYouWatchedPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            // Seed on the user's 5 most recently *watched* movies (by LastPlayedDate,
            // falling back to DateAdded when the played date is unknown), not just the
            // single most-recently-indexed one. Recommendations are ranked by the best
            // similarity across all 5 seeds so the playlist reflects recent taste.
            var watchedWithDates = await _watchHistoryService.GetWatchedMoviesWithDatesAsync(userId, cancellationToken);
            if (!watchedWithDates.Any()) return;

            var recentSeeds = watchedWithDates
                .OrderByDescending(w => w.WatchedAt ?? w.Movie.DateAdded)
                .Take(5)
                .Select(w => w.Movie)
                .ToList();

            var mostRecent = recentSeeds.First();

            // Best similarity per unwatched movie across the 5 recent seeds.
            var bestSim = new Dictionary<Guid, double>();
            foreach (var seed in recentSeeds)
            {
                foreach (var m in unwatched)
                {
                    var sim = _similarityEngine.CalculateSimilarity(seed, m);
                    if (!bestSim.TryGetValue(m.ItemId, out double current) || sim > current)
                        bestSim[m.ItemId] = sim;
                }
            }

            var picks = bestSim
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => kv.Key)
                .ToList();

            await CreateOrUpdateJellyfinPlaylistAsync(userId, $"Because You Watched {mostRecent.Title}", picks, cancellationToken);
        }
        
        private async Task GenerateHiddenGemsPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            // Filter movies with high acclaim but obscure/niche tags
            var gems = unwatched
                .Where(m => m.CriticalAcclaimScore >= 7)
                .OrderBy(m => Guid.NewGuid()) // randomize for now
                .Take(15)
                .Select(m => m.ItemId)
                .ToList();
                
            await CreateOrUpdateJellyfinPlaylistAsync(userId, "Hidden Gems", gems, cancellationToken);
        }

        private async Task GenerateRecentlyAddedPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            var recent = unwatched
                .OrderByDescending(m => m.DateAdded)
                .Take(15)
                .Select(m => m.ItemId)
                .ToList();
                
            await CreateOrUpdateJellyfinPlaylistAsync(userId, "Recently Added", recent, cancellationToken);
        }

        private async Task GenerateSubcategoryPlaylistsAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            if (profile.SubcategoryPreferences.Any() && _config.EnableSubcategory)
            {
                // Pick top familiar subcategory
                var topSubcategory = profile.SubcategoryPreferences.OrderByDescending(x => x.Value).First().Key;
                
                var familiarPicks = unwatched
                    .Where(m => !string.IsNullOrEmpty(m.Subcategories) && m.Subcategories.Contains(topSubcategory, StringComparison.OrdinalIgnoreCase))
                    .Take(15)
                    .Select(m => m.ItemId)
                    .ToList();
                    
                if (familiarPicks.Any())
                    await CreateOrUpdateJellyfinPlaylistAsync(userId, $"{topSubcategory} For You", familiarPicks, cancellationToken);
            }
            
            if (_config.EnableDiscover)
            {
                // Mock discovering an unfamiliar subcategory
                var discoverPicks = unwatched.OrderBy(m => Guid.NewGuid()).Take(8).Select(m => m.ItemId).ToList();
                await CreateOrUpdateJellyfinPlaylistAsync(userId, "Discover: Hidden World", discoverPicks, cancellationToken);
            }
        }
        
        private async Task GenerateWildCardPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            var wildPicks = unwatched.OrderBy(m => Guid.NewGuid()).Take(10).Select(m => m.ItemId).ToList();
            await CreateOrUpdateJellyfinPlaylistAsync(userId, "Wild Card", wildPicks, cancellationToken);
        }
        
        private async Task GenerateWatchlistPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            // Sync the user's watchlist (from the JSON URL or CSV they provided in
            // config) into matched library ItemIds, then build the playlist from those.
            await _letterboxdService.SyncWatchlistAsync(userId, cancellationToken);

            var userConfig = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (userConfig == null || string.IsNullOrWhiteSpace(userConfig.MatchedItemIds))
            {
                _logger.LogInformation("No matched watchlist items for user {UserId}; skipping 'From Your Watchlist'.", userId);
                return;
            }

            List<Guid> matchedIds;
            try
            {
                matchedIds = JsonSerializer.Deserialize<List<Guid>>(userConfig.MatchedItemIds) ?? new List<Guid>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse MatchedItemIds for user {UserId}", userId);
                return;
            }

            if (!matchedIds.Any())
            {
                _logger.LogInformation("Watchlist for user {UserId} matched 0 library items; skipping.", userId);
                return;
            }

            await CreateOrUpdateJellyfinPlaylistAsync(userId, "From Your Watchlist", matchedIds, cancellationToken);
        }

        private double ScoreMovieAgainstProfile(MovieMetadata movie, TasteProfile profile)
        {
            // Taste-matched scoring: how well this movie's tags align with the
            // user's learned preferences (weighted subcategory + mood affinity).
            double subScore = 0.0;
            if (!string.IsNullOrWhiteSpace(movie.Subcategories) && profile.SubcategoryPreferences.Any())
            {
                try
                {
                    var subs = JsonSerializer.Deserialize<List<string>>(movie.Subcategories);
                    if (subs != null && subs.Count > 0)
                    {
                        double matched = 0.0;
                        foreach (var s in subs)
                        {
                            if (profile.SubcategoryPreferences.TryGetValue(s, out double w))
                                matched += w;
                        }
                        // Average affinity across the movie's subcategories, capped at 1.0
                        subScore = Math.Min(1.0, matched / subs.Count);
                    }
                }
                catch { /* ignore parse errors */ }
            }

            double moodScore = 0.0;
            if (!string.IsNullOrWhiteSpace(movie.Moods) && profile.MoodPreferences.Any())
            {
                try
                {
                    var moods = JsonSerializer.Deserialize<List<string>>(movie.Moods);
                    if (moods != null && moods.Count > 0)
                    {
                        double matched = 0.0;
                        foreach (var m in moods)
                        {
                            if (profile.MoodPreferences.TryGetValue(m, out double w))
                                matched += w;
                        }
                        moodScore = Math.Min(1.0, matched / moods.Count);
                    }
                }
                catch { /* ignore parse errors */ }
            }

            // Subcategories are the strongest taste signal; moods refine it.
            return 0.7 * subScore + 0.3 * moodScore;
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

            // Find this user's own playlist by name (scoped per-user so refreshes
            // for different users don't delete each other's recommendation playlists).
            var existingPlaylist = allPlaylists.FirstOrDefault(p => p.Name == name && p.OwnerUserId == userId);
            if (existingPlaylist != null)
            {
                _libraryManager.DeleteItem(existingPlaylist, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true });
            }

            if (itemIds.Any())
            {
                var req = new MediaBrowser.Model.Playlists.PlaylistCreationRequest
                {
                    Name = name,
                    UserId = userId,
                    ItemIdList = itemIds,
                    Public = false
                };
                
                var result = _playlistManager.CreatePlaylist(req);
                _logger.LogInformation("Created playlist '{Name}' for user {UserId} with {Count} items (Result Id: {ResultId}).", name, userId, itemIds.Count, result.Id);
            }
            else
            {
                _logger.LogInformation("Skipped creating playlist '{Name}' because there were no items.", name);
            }
        }
    }
}
