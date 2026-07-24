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
            _logger.LogInformation("Applying dynamic rating update for UserId {UserId}, watched MovieId {MovieId}", userId, watchedMovieId);

            // Load the user's current affinities (or start empty).
            var affinities = await _movieStore.GetAffinitiesAsync(userId, cancellationToken);
            var now = DateTime.UtcNow;
            var penaltyUntilIso = now.AddHours(_config.CoolingPeriodCycles * _config.PlaylistRefreshHours)
                                      .ToString("o");

            // 1. Find which playlists the watched movie currently lives in (the "source" playlists).
            var sourcePlaylistMovies = GetMoviesInPlaylistsContaining(userId, watchedMovieId);
            var changed = new List<MovieAffinity>();

            // 2. PUNISH siblings: every OTHER movie in a source playlist gets a penalty
            //    (lower affinity) and a temporary ban via PenaltyUntil (cooling period).
            foreach (var siblingId in sourcePlaylistMovies.Where(id => id != watchedMovieId))
            {
                var row = GetOrCreateAffinity(affinities, userId, siblingId);
                row.Affinity = Clamp(row.Affinity + _config.PunishmentPenalty, -1.0, 1.0);
                row.PenaltyUntil = penaltyUntilIso;
                row.LastUpdated = now.ToString("o");
                changed.Add(row);
            }

            // 3. REWARD similar movies: the watched movie's nearest neighbours get a small
            //    boost (and any active penalty is pulled forward / reduced) — implements the
            //    "watch a related movie -> penalty reduced" behaviour from the README.
            var allMovies = await _movieStore.GetAllMoviesAsync(cancellationToken);
            var watchedMovie = allMovies.FirstOrDefault(m => m.ItemId == watchedMovieId);
            if (watchedMovie != null)
            {
                var neighbours = allMovies
                    .Where(m => m.ItemId != watchedMovieId && m.IsClassified)
                    .Select(m => new { M = m, Sim = _similarityEngine.CalculateSimilarity(watchedMovie, m) })
                    .Where(x => x.Sim > 0.0)
                    .OrderByDescending(x => x.Sim)
                    .Take(25)
                    .ToList();

                foreach (var n in neighbours)
                {
                    var row = GetOrCreateAffinity(affinities, userId, n.M.ItemId);
                    row.Affinity = Clamp(row.Affinity + _config.RewardBoost, -1.0, 1.0);
                    // Reduce an active penalty if present (pull it forward toward now).
                    if (!string.IsNullOrEmpty(row.PenaltyUntil) &&
                        DateTime.TryParse(row.PenaltyUntil, out var pu) && pu > now)
                    {
                        row.PenaltyUntil = now.AddHours(
                            Math.Max(0, (pu - now).TotalHours / 2.0)).ToString("o");
                    }
                    row.LastUpdated = now.ToString("o");
                    changed.Add(row);
                }
            }

            if (changed.Any())
                await _movieStore.UpsertAffinitiesAsync(changed, cancellationToken);

            // 4. Rebuild the user's playlists (refresh READS the new affinities).
            await RefreshUserPlaylistsAsync(userId, cancellationToken);
        }

        // Returns the ItemIds of all OTHER movies sharing a playlist with the given movie,
        // limited to playlists owned by the user.
        private HashSet<Guid> GetMoviesInPlaylistsContaining(Guid userId, Guid movieId)
        {
            var result = new HashSet<Guid>();
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Playlist>().Where(p => p.OwnerUserId == userId).ToList();

            foreach (var pl in playlists)
            {
                // A playlist's children are items whose ParentId is the playlist.
                var childIds = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = pl.Id,
                    IsVirtualItem = false,
                    Recursive = false
                }).Select(i => i.Id).ToHashSet();

                if (childIds.Contains(movieId))
                {
                    foreach (var id in childIds)
                        if (id != movieId) result.Add(id);
                }
            }
            return result;
        }

        private static MovieAffinity GetOrCreateAffinity(Dictionary<Guid, MovieAffinity> dict, Guid userId, Guid itemId)
        {
            if (dict.TryGetValue(itemId, out var existing)) return existing;
            var fresh = new MovieAffinity
            {
                UserId = userId.ToString(),
                ItemId = itemId.ToString(),
                Affinity = 0.0,
                PenaltyUntil = null,
                LastUpdated = DateTime.UtcNow.ToString("o")
            };
            dict[itemId] = fresh;
            return fresh;
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
            var (unwatchedMovies, affinities) = await GetUnwatchedClassifiedMoviesAsync(userId, cancellationToken);
            
            if (_config.EnableForYou)
                await GenerateForYouPlaylistAsync(userId, tasteProfile, unwatchedMovies, affinities, cancellationToken);

            if (_config.EnableBecauseYouWatched)
                await GenerateBecauseYouWatchedPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableHiddenGems)
                await GenerateHiddenGemsPlaylistAsync(userId, unwatchedMovies, profile, affinities, cancellationToken);
                
            if (_config.EnableRecentlyAdded)
                await GenerateRecentlyAddedPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableSubcategory || _config.EnableDiscover)
                await GenerateSubcategoryPlaylistsAsync(userId, tasteProfile, unwatchedMovies, affinities, cancellationToken);
                
            if (_config.EnableWildCard)
                await GenerateWildCardPlaylistAsync(userId, tasteProfile, unwatchedMovies, affinities, cancellationToken);

            // Watchlist is handled separately if the user enabled it
            var userConfig = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (userConfig != null && userConfig.EnableWatchlistPlaylist)
            {
                await GenerateWatchlistPlaylistAsync(userId, unwatchedMovies, cancellationToken);
            }
            
            _logger.LogInformation("Finished refreshing playlists for user {UserId}", userId);
        }

        // Deletes ALL playlists owned by a user (complete wipe) before regenerating,
        // so the user starts from a totally clean slate. User-created playlists are
        // also removed, as intended for this deployment.
        private async Task DeleteUserRecommendationPlaylistsAsync(Guid userId, CancellationToken cancellationToken)
        {
            var allPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Playlist>().ToList();

            foreach (var playlist in allPlaylists.Where(p => p.OwnerUserId == userId))
            {
                _logger.LogInformation("Deleting playlist '{Name}' for user {UserId} (clean slate).", playlist.Name, userId);
                _libraryManager.DeleteItem(playlist, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true });
            }

            await Task.CompletedTask;
        }

        private async Task GenerateForYouPlaylistAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, Dictionary<Guid, MovieAffinity> affinities, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            // 75% taste-matched, 25% exploration (from _config.DiversityWeight)
            int totalSize = _config.MaxMoviesPerPlaylist;
            int exploreSize = (int)(totalSize * (_config.DiversityWeight / 100.0));
            int tasteSize = totalSize - exploreSize;

            // Score movies based on taste profile + review nudging + dynamic affinity/new-movie nudge.
            // If the user has no watch history yet, the taste profile is empty, so
            // fall back to critical acclaim so "For You" still surfaces quality picks.
            bool hasTaste = profile.SubcategoryPreferences.Any() || profile.MoodPreferences.Any();
            var scoredMovies = unwatched.Select(m => new
            {
                Movie = m,
                Score = (hasTaste ? ScoreMovieAgainstProfile(m, profile) : 0.0)
                        + CalculateReviewNudge(m)
                        + (hasTaste ? 0.0 : m.CriticalAcclaimScore / 10.0)
                        + Clamp(GetEffectiveAffinity(affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                        + GetNewMovieBoost(m, now)
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
        
        private async Task GenerateHiddenGemsPlaylistAsync(guid userId, List<MovieMetadata> unwatched, TasteProfile profile, Dictionary<Guid, MovieAffinity> affinities, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            // "Hidden Gems" = high acclaim AND unfamiliar to the user (subcategories
            // the user does NOT already watch a lot). This is the opposite of the
            // familiar "For You" — it surfaces quality films outside the comfort zone.
            var familiarSubs = TopSubcategories(profile, 5); // most-watched subcats
            var gems = unwatched
                .Where(m => m.CriticalAcclaimScore >= 7)
                .Where(m => !SharesAnySubcategory(m, familiarSubs)) // unfamiliar = hidden
                .Select(m => new
                {
                    M = m,
                    Score = m.CriticalAcclaimScore / 10.0
                            + Clamp(GetEffectiveAffinity(affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoost(m, now)
                })
                .OrderByDescending(x => x.Score)
                .Take(15)
                .Select(x => x.M.ItemId)
                .ToList();

            await CreateOrUpdateJellyfinPlaylistAsync(userId, "Hidden Gems", gems, cancellationToken);
        }

        // Returns the user's most-preferred subcategory names (by taste profile weight).
        private static HashSet<string> TopSubcategories(TasteProfile profile, int count)
        {
            if (profile?.SubcategoryPreferences == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return profile.SubcategoryPreferences
                .OrderByDescending(kv => kv.Value)
                .Take(count)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool SharesAnySubcategory(MovieMetadata m, HashSet<string> subs)
        {
            if (subs.Count == 0 || string.IsNullOrWhiteSpace(m.Subcategories)) return false;
            try
            {
                var movieSubs = JsonSerializer.Deserialize<List<string>>(m.Subcategories)?
                    .Select(s => s.ToLowerInvariant()).ToHashSet() ?? new HashSet<string>();
                return movieSubs.Overlaps(subs.Select(s => s.ToLowerInvariant()));
            }
            catch { return false; }
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

        private async Task GenerateSubcategoryPlaylistsAsync(guid userId, TasteProfile profile, List<MovieMetadata> unwatched, Dictionary<Guid, MovieAffinity> affinities, CancellationToken cancellationToken)
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
                // Discover = the user's LEAST-explored subcategories (gateway into the unknown).
                // Surface movies from those subcats, ranked by acclaim + learned affinity +
                // new-movie nudge, so they're adjacent to taste, not random.
                var discovered = DiscoverPicks(unwatched, profile, affinities, 8);
                if (discovered.Any())
                    await CreateOrUpdateJellyfinPlaylistAsync(userId, "Discover: Hidden World", discovered, cancellationToken);
            }
        }

        // Movies from the user's least-weighted subcategories, ranked by acclaim + affinity.
        private List<Guid> DiscoverPicks(List<MovieMetadata> unwatched, TasteProfile profile, Dictionary<Guid, MovieAffinity> affinities, int count)
        {
            var now = DateTime.UtcNow;
            IEnumerable<string> leastFamiliar;
            if (profile.SubcategoryPreferences.Any())
                leastFamiliar = profile.SubcategoryPreferences
                    .OrderBy(kv => kv.Value)          // least preferred first
                    .Take(3)
                    .Select(kv => kv.Key);
            else
                leastFamiliar = Enumerable.Empty<string>(); // cold user: fall back to acclaim below

            var leastSet = leastFamiliar
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();

            return unwatched
                .Where(m => m.IsClassified && (!leastSet.Any() || SharesAnySubcategory(m, leastSet)))
                .Select(m => new
                {
                    M = m,
                    Score = (leastSet.Any() && SharesAnySubcategory(m, leastSet) ? 0.5 : 0.0)
                            + m.CriticalAcclaimScore / 10.0
                            + Clamp(GetEffectiveAffinity(affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoost(m, now)
                })
                .OrderByDescending(x => x.Score)
                .Take(count)
                .Select(x => x.M.ItemId)
                .ToList();
        }

        private async Task GenerateWildCardPlaylistAsync(guid userId, TasteProfile profile, List<MovieMetadata> unwatched, Dictionary<Guid, MovieAffinity> affinities, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            // Wild Card = 100% exploration: the user's LEAST-explored subcategory,
            // only high-acclaim films, ranked by acclaim + affinity + new-movie nudge.
            var leastFamiliar = profile.SubcategoryPreferences.Any()
                ? profile.SubcategoryPreferences.OrderBy(kv => kv.Value).First().Key
                : null;

            var wildPicks = unwatched
                .Where(m => m.CriticalAcclaimScore >= 7)
                .Where(m => leastFamiliar == null || (m.Subcategories ?? "").Contains(leastFamiliar, StringComparison.OrdinalIgnoreCase))
                .Select(m => new
                {
                    M = m,
                    Score = m.CriticalAcclaimScore / 10.0
                            + Clamp(GetEffectiveAffinity(affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoost(m, now)
                })
                .OrderByDescending(x => x.Score)
                .Take(10)
                .Select(x => x.M.ItemId)
                .ToList();

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

        // ---- Dynamic rating helpers (v1.3.0) ----

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);

        // Effective affinity after lazy time-decay. Never writes — pure read-time computation.
        private double GetEffectiveAffinity(Dictionary<Guid, MovieAffinity> affinities, Guid itemId)
        {
            if (!affinities.TryGetValue(itemId, out var row) || row == null) return 0.0;
            if (row.LastUpdated == null) return row.Affinity;
            if (!DateTime.TryParse(row.LastUpdated, out var updated)) return row.Affinity;
            var ageDays = (DateTime.UtcNow - updated).TotalDays;
            if (ageDays <= 0) return row.Affinity;
            return row.Affinity * Math.Exp(-ageDays / Math.Max(1.0, _config.AffinityDecayHalfLifeDays));
        }

        // A movie is excluded from recommendations while its cooling-period ban is active.
        private bool IsPenalized(Dictionary<Guid, MovieAffinity> affinities, Guid itemId, DateTime now)
        {
            if (!affinities.TryGetValue(itemId, out var row) || string.IsNullOrEmpty(row?.PenaltyUntil)) return false;
            return DateTime.TryParse(row.PenaltyUntil, out var until) && until > now;
        }

        // Small recency nudge so freshly-added movies surface beyond "Recently Added".
        private double GetNewMovieBoost(MovieMetadata movie, DateTime now)
        {
            if (_config.NewMovieBoostDays <= 0) return 0.0;
            var ageDays = (now - movie.DateAdded).TotalDays;
            if (ageDays < 0 || ageDays > _config.NewMovieBoostDays) return 0.0;
            // Linear falloff across the window, capped by AffinityRankWeight.
            var factor = 1.0 - (ageDays / _config.NewMovieBoostDays);
            return Clamp(_config.NewMovieBoostWeight * factor, 0.0, _config.AffinityRankWeight);
        }

        // Returns unwatched, classified movies EXCLUDING any currently penalized
        // (cooling-period ban active), plus the user's affinity map for scoring.
        private async Task<(List<MovieMetadata> Movies, Dictionary<Guid, MovieAffinity> Affinities)>
            GetUnwatchedClassifiedMoviesAsync(Guid userId, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var affinities = await _movieStore.GetAffinitiesAsync(userId, cancellationToken);
            var watched = await _watchHistoryService.GetWatchedMoviesAsync(userId, cancellationToken);
            var watchedIds = watched.Select(m => m.ItemId).ToHashSet();

            var all = await _movieStore.GetAllMoviesAsync(cancellationToken);
            var result = all
                .Where(m => m.IsClassified
                            && !watchedIds.Contains(m.ItemId)
                            && !IsPenalized(affinities, m.ItemId, now))
                .ToList();

            return (result, affinities);
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
