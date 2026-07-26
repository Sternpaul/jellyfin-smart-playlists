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
        private readonly TmdbKeywordService _tmdbKeywordService;
        private PluginConfiguration _config => Plugin.Instance!.Configuration;
        private readonly ILogger<PlaylistEngine> _logger;

        // v1.5.0: observability. Records WHY each movie was excluded from a user's
        // playlists on the last refresh, so the config page can show "what's happening".
        // In-memory only (no DB); rebuilt every refresh. Keyed by user, then ItemId.
        private readonly Dictionary<Guid, Dictionary<Guid, List<string>>> _lastExclusions = new();
        private readonly object _exclusionsLock = new();

        // v1.5.3: per-user consumption-rate decay factor (half-life multiplier).
        // Computed once per refresh from the user's recent weekly watch rate.
        private readonly Dictionary<Guid, double> _decayFactor = new();
        private readonly object _decayLock = new();
        private readonly RefreshExecutionGate _refreshExecutionGate = new();

        public PlaylistEngine(
            IPlaylistManager playlistManager,
            ILibraryManager libraryManager,
            MovieStore movieStore,
            WatchHistoryService watchHistoryService,
            SimilarityEngine similarityEngine,
            LetterboxdService letterboxdService,
            TmdbKeywordService tmdbKeywordService,
            ILogger<PlaylistEngine> logger)
        {
            _playlistManager = playlistManager;
            _libraryManager = libraryManager;
            _movieStore = movieStore;
            _watchHistoryService = watchHistoryService;
            _similarityEngine = similarityEngine;
            _letterboxdService = letterboxdService;
            _tmdbKeywordService = tmdbKeywordService;
            _logger = logger;
            
            _watchHistoryService.WatchEventEmitted += OnMovieWatched;
        }

        private async void OnMovieWatched(object? sender, WatchEventArgs e)
        {
            try
            {
                // v1.5.1: completion-weighted learning. A watch only counts as a real
                // signal (penalty + reward) if playback reached MinCompletionPercent.
                // Quick glances / tests (< threshold) are ignored — no penalty.
                if (e.PlaybackPercentage.HasValue && e.PlaybackPercentage < _config.MinCompletionPercent)
                {
                    _logger.LogInformation(
                        "Watch of {MovieId} by {UserId} was only {Pct}% complete (< {Min}% threshold); ignoring as a learning signal.",
                        e.MovieId, e.UserId, e.PlaybackPercentage, _config.MinCompletionPercent);
                    return;
                }

                await HandlePunishmentAndRebuildAsync(e.UserId, e.MovieId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling watch event for punishment rebuild.");
            }
        }

        private Task HandlePunishmentAndRebuildAsync(Guid userId, Guid watchedMovieId, CancellationToken cancellationToken)
            => _refreshExecutionGate.RunAsync(
                gateToken => HandlePunishmentAndRebuildCoreAsync(userId, watchedMovieId, gateToken),
                cancellationToken);

        private async Task HandlePunishmentAndRebuildCoreAsync(Guid userId, Guid watchedMovieId, CancellationToken cancellationToken)
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
            await RefreshUserPlaylistsCoreAsync(userId, cancellationToken);
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


        // v1.5.9: apply per-user exclusions immediately when config is saved, so the
        // disabled users' playlists disappear without waiting for the next 12h refresh.
        public Task ApplyExclusionsNowAsync(IEnumerable<Guid> disabledUserIds, CancellationToken cancellationToken = default)
            => _refreshExecutionGate.RunAsync(
                gateToken => ApplyExclusionsNowCoreAsync(disabledUserIds, gateToken),
                cancellationToken);

        private async Task ApplyExclusionsNowCoreAsync(IEnumerable<Guid> disabledUserIds, CancellationToken cancellationToken)
        {
            foreach (var userId in disabledUserIds)
            {
                if (cancellationToken.IsCancellationRequested) break;
                _logger.LogInformation("User {UserId} disabled via config; removing their recommendation playlists.", userId);
                await DeleteUserRecommendationPlaylistsAsync(userId, cancellationToken);
            }
        }

        public Task RefreshUserPlaylistsAsync(Guid userId, CancellationToken cancellationToken = default)
            => _refreshExecutionGate.RunAsync(
                gateToken => RefreshUserPlaylistsCoreAsync(userId, gateToken),
                cancellationToken);

        private async Task RefreshUserPlaylistsCoreAsync(Guid userId, CancellationToken cancellationToken)
        {
            // v1.5.14: push the configured keyword weight into the similarity engine
            // so Because You Watched respects TMDB keyword overlap.
            _similarityEngine.KeywordWeight = _config.KeywordWeight;

            // NOTE: TMDB keyword enrichment runs ONCE per refresh (see EnrichKeywordsOnceAsync),
            // not here, so it isn't repeated for every user. It writes to the shared DB.


            // Respect per-user exclusions configured by the admin.
            if (_config.DisabledUserIds != null &&
                _config.DisabledUserIds.Any(id => Guid.TryParse(id, out var disabledId) && disabledId == userId))
            {
                _logger.LogInformation("User {UserId} is disabled; removing their recommendation playlists.", userId);
                await DeleteUserRecommendationPlaylistsAsync(userId, cancellationToken);
                return;
            }

            // Clean slate: remove any existing recommendation playlists for this user
            // before regenerating, so stale/disabled/renamed ones never linger.
            await DeleteUserRecommendationPlaylistsAsync(userId, cancellationToken);

            // v1.5.0: reset the per-user exclusion log so the debug snapshot reflects THIS refresh.
            lock (_exclusionsLock)
            {
                _lastExclusions[userId] = new Dictionary<Guid, List<string>>();
            }

            _logger.LogInformation("Refreshing playlists for user {UserId}", userId);
            
            // v1.5.2: track movies already placed in a discovery playlist so the same
            // film never appears in two of a user's playlists. BYW is exempt (it's an
            // intentional similarity list); RecentlyAdded/Watchlist are their own sources.
            var claimed = new HashSet<Guid>();

            var tasteProfile = await _watchHistoryService.GetUserTasteProfileAsync(userId, cancellationToken);
            var (unwatchedMovies, affinities) = await GetUnwatchedClassifiedMoviesAsync(userId, cancellationToken);

            // v1.5.12: pull the user's Letterboxd ratings (public page scrape) and use
            // them as the dominant recommendation signal. Failures are swallowed inside.
            // If no username is configured, clear any stale ratings so NO ratings weight
            // is ever applied for this user.
            var userRatingsConfig = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (string.IsNullOrWhiteSpace(userRatingsConfig?.RatingsJsonUrl))
                await _movieStore.SaveUserRatingsAsync(userId, Array.Empty<UserRating>(), cancellationToken);
            else
            {
                try { await _letterboxdService.FetchRatingsFromJsonAsync(userId, cancellationToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Ratings fetch failed for {UserId}; continuing without ratings.", userId); }
            }
            var ratings = await _movieStore.GetUserRatingsAsync(userId, cancellationToken);

            // v1.5.4: periodically snapshot the taste profile so the config page can
            // show how tastes drift over time (weekly, at most).
            await MaybeSaveTasteSnapshotAsync(userId, tasteProfile, cancellationToken);
            // v1.5.3: compute this user's consumption-rate decay factor. Faster watchers
            // get shorter effective half-lives (fresher playlists); slow watchers slower.
            ComputeDecayFactor(userId, cancellationToken);
            if (_config.EnableForYou)
                claimed.UnionWith(await GenerateForYouPlaylistAsync(userId, tasteProfile, unwatchedMovies, affinities, ratings, claimed, cancellationToken));

            if (_config.EnableBecauseYouWatched)
                await GenerateBecauseYouWatchedPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableHiddenGems)
                claimed.UnionWith(await GenerateHiddenGemsPlaylistAsync(userId, unwatchedMovies, tasteProfile, affinities, claimed, cancellationToken));
                
            if (_config.EnableRecentlyAdded)
                await GenerateRecentlyAddedPlaylistAsync(userId, unwatchedMovies, cancellationToken);
                
            if (_config.EnableSubcategory || _config.EnableDiscover)
                claimed.UnionWith(await GenerateSubcategoryPlaylistsAsync(userId, tasteProfile, unwatchedMovies, affinities, claimed, cancellationToken));
                
            if (_config.EnableWildCard)
                claimed.UnionWith(await GenerateWildCardPlaylistAsync(userId, tasteProfile, unwatchedMovies, affinities, claimed, cancellationToken));

            // Watchlist and ratings playlists are handled separately if the user enabled them
            var userConfig = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (userConfig != null && userConfig.EnableWatchlistPlaylist)
            {
                await GenerateWatchlistPlaylistAsync(userId, unwatchedMovies, cancellationToken);
            }
            if (userConfig != null && userConfig.EnableRatingsPlaylist && ratings.Count > 0)
            {
                await GenerateRatingsPlaylistAsync(userId, ratings, cancellationToken);
            }
            
            _logger.LogInformation("Finished refreshing playlists for user {UserId}", userId);
        }

        // v1.5.39: Jellyfin 10.11 does NOT set OwnerUserId from PlaylistCreationRequest.UserId —
        // plugin-created playlists end up with OwnerUserId == Guid.Empty. The old owner-scoped
        // delete/find matched nothing, so every refresh created a NEW playlist (Jellyfin appends
        // 1, 11, 111... to the path) and ghosts accumulated forever, visible to every user.
        // Fix: (a) CreateOrUpdateJellyfinPlaylistAsync now explicitly sets OwnerUserId after
        // creation; (b) cleanup also removes OWNERLESS playlists whose name matches our
        // recommendation patterns, which migrates existing ghost playlists away.
        private static bool IsRecommendationPlaylistName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            // Jellyfin dedupes colliding paths by appending digits ("For You11"); strip them.
            var baseName = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd();
            if (baseName.StartsWith("Because You Watched", StringComparison.OrdinalIgnoreCase)) return true;
            if (baseName.EndsWith("For You", StringComparison.OrdinalIgnoreCase)) return true; // "For You", "drama For You", ...
            return baseName.Equals("Hidden Gems", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("Discover: Hidden World", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("Discover  Hidden World", StringComparison.OrdinalIgnoreCase) // FS-sanitized ghost
                || baseName.Equals("Wild Card", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("Recently Added", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("From Your Watchlist", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("Highly Rated by You", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldDeleteRecommendationPlaylist(Guid ownerUserId, string? name, Guid targetUserId)
        {
            return IsRecommendationPlaylistName(name)
                && (ownerUserId == targetUserId || ownerUserId == Guid.Empty);
        }

        // Deletes only this plugin's recommendation playlists for the target user.
        // Unrelated user-created playlists must survive a refresh. Ownerless plugin
        // recommendation patterns are also removed to migrate pre-v1.5.39 ghosts.
        private async Task DeleteUserRecommendationPlaylistsAsync(Guid userId, CancellationToken cancellationToken)
        {
            var allPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Playlist>().ToList();

            foreach (var playlist in allPlaylists.Where(p =>
                         ShouldDeleteRecommendationPlaylist(p.OwnerUserId, p.Name, userId)))
            {
                _logger.LogInformation("Deleting playlist '{Name}' for user {UserId} (clean slate).", playlist.Name, userId);
                _libraryManager.DeleteItem(playlist, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true });
            }

            await Task.CompletedTask;
        }

        private async Task<List<Guid>> GenerateForYouPlaylistAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, Dictionary<Guid, MovieAffinity> affinities, Dictionary<Guid, double> ratings, HashSet<Guid> claimed, CancellationToken cancellationToken)
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
            // v1.5.12: Letterboxd ratings are the dominant signal. A 5-star rating
            // contributes up to RatingWeight to the score; other terms are nudges around it.
            double ratingW = _config.RatingWeight;
            var scoredMovies = unwatched
                .Where(m => !claimed.Contains(m.ItemId))
                .Select(m => new
                {
                    Movie = m,
                    Score = (hasTaste ? ScoreMovieAgainstProfile(m, profile) : 0.0)
                            + CalculateReviewNudge(m)
                            + (hasTaste ? 0.0 : m.CriticalAcclaimScore / 10.0)
                            + Clamp(GetEffectiveAffinity(userId, affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoostByFit(m, profile, now)
                            + GetNoveltyBonus(userId, affinities, m.ItemId, now)
                            + GetSoftPenalty(affinities, m.ItemId, now)
                            + (ratings.TryGetValue(m.ItemId, out var r) ? ratingW * (r / 5.0) : 0.0)
                }).OrderByDescending(x => x.Score).ToList();

            var tastePicks = scoredMovies.Take(tasteSize).Select(x => x.Movie.ItemId).ToList();
            
            // Exploration picks (least matched)
            var explorePicks = scoredMovies.OrderBy(x => x.Score).Take(exploreSize).Select(x => x.Movie.ItemId).ToList();
            
            var finalPicks = tastePicks.Concat(explorePicks).ToList();

            // Anti-bubble: cap how much any single subcategory may occupy.
            finalPicks = ApplyDiversityCap(finalPicks, unwatched, _config.DiversityCapPercent, userId);

            await CreateOrUpdateJellyfinPlaylistAsync(userId, "For You", finalPicks, cancellationToken);
            return finalPicks;
        }

        // Enforces that no single subcategory exceeds maxPercent of the playlist.
        // Overflow picks are swapped for the next-best movie from a different subcategory.
        // v1.5.0: also records "over diversity cap" exclusions for the debug view.
        private List<Guid> ApplyDiversityCap(List<Guid> picks, List<MovieMetadata> pool, int maxPercent, Guid userId)
        {
            if (maxPercent >= 100 || picks.Count == 0) return picks;
            var byId = pool.ToDictionary(m => m.ItemId);
            int cap = (int)Math.Ceiling(picks.Count * (maxPercent / 100.0));
            if (cap >= picks.Count) return picks;

            var result = new List<Guid>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var poolLeft = new List<MovieMetadata>(pool.Where(m => !picks.Contains(m.ItemId)));

            foreach (var id in picks)
            {
                var sub = byId.TryGetValue(id, out var mv) ? PrimarySubcategory(mv) : null;
                if (sub != null && counts.TryGetValue(sub, out int c) && c >= cap)
                {
                    // Over cap: record why this one was dropped, then try to swap.
                    RecordExclusion(userId, id, $"Over diversity cap ({sub})");
                    var swap = poolLeft.FirstOrDefault(m =>
                    {
                        var psub = PrimarySubcategory(m);
                        return psub == null || !counts.ContainsKey(psub) || counts[psub] < cap;
                    });
                    if (swap != null)
                    {
                        poolLeft.Remove(swap);
                        result.Add(swap.ItemId);
                        var ss = PrimarySubcategory(swap);
                        if (ss != null) counts[ss] = counts.ContainsKey(ss) ? counts[ss] + 1 : 1;
                        continue;
                    }
                }
                result.Add(id);
                if (sub != null) counts[sub] = counts.TryGetValue(sub, out int cc) ? cc + 1 : 1;
            }
            return result;
        }

        private static string? PrimarySubcategory(MovieMetadata m)
        {
            if (string.IsNullOrWhiteSpace(m.Subcategories)) return null;
            try
            {
                var subs = JsonSerializer.Deserialize<List<string>>(m.Subcategories);
                return subs != null && subs.Count > 0 ? subs[0] : null;
            }
            catch { return null; }
        }

        private async Task GenerateBecauseYouWatchedPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, CancellationToken cancellationToken)
        {
            // Seed on the user's 5 most recently *watched* movies (by LastPlayedDate,
            // falling back to DateAdded when the played date is unknown), not just the
            // single most-recently-indexed one. Recommendations are ranked by the best
            // similarity across all 5 seeds so the playlist reflects recent taste.
            //
            // The playlist is titled after the seed that actually *dominates* the final
            // picks (count of top picks it contributed), NOT merely the most-recently
            // watched seed — otherwise the label can describe a movie the list isn't
            // really about (e.g. titled after the last watch while the picks are all
            // similar to an older seed). Ties break toward the more recent seed.
            var watchedWithDates = await _watchHistoryService.GetWatchedMoviesWithDatesAsync(userId, cancellationToken);
            if (!watchedWithDates.Any()) return;

            var recentSeeds = watchedWithDates
                .OrderByDescending(w => w.WatchedAt ?? w.Movie.DateAdded)
                .Take(5)
                .Select(w => w.Movie)
                .ToList();

            // Best similarity per unwatched movie across the 5 recent seeds, remembering
            // which seed produced that best score.
            var bestSim = new Dictionary<Guid, double>();
            var bestSeed = new Dictionary<Guid, MovieMetadata>();
            foreach (var seed in recentSeeds)
            {
                foreach (var m in unwatched)
                {
                    var sim = _similarityEngine.CalculateSimilarity(seed, m);
                    if (!bestSim.TryGetValue(m.ItemId, out double current) || sim > current)
                    {
                        bestSim[m.ItemId] = sim;
                        bestSeed[m.ItemId] = seed;
                    }
                }
            }

            var picks = bestSim
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => kv.Key)
                .ToList();

            // Name the playlist after the seed that contributed the most of the chosen
            // picks (most-recent seed wins ties), so the title matches the content.
            var anchor = SelectBecauseYouWatchedAnchor(picks, bestSeed, recentSeeds);
            if (anchor == null)
                return;

            await CreateOrUpdateJellyfinPlaylistAsync(userId, $"Because You Watched {anchor.Title}", picks, cancellationToken);
        }

        private static MovieMetadata? SelectBecauseYouWatchedAnchor(
            List<Guid> picks,
            Dictionary<Guid, MovieMetadata> bestSeed,
            List<MovieMetadata> recentSeeds)
        {
            if (picks.Count == 0)
                return null;

            return picks
                .Where(bestSeed.ContainsKey)
                .GroupBy(itemId => bestSeed[itemId])
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => recentSeeds.IndexOf(group.Key))
                .Select(group => group.Key)
                .FirstOrDefault();
        }
        
        private async Task<List<Guid>> GenerateHiddenGemsPlaylistAsync(Guid userId, List<MovieMetadata> unwatched, TasteProfile profile, Dictionary<Guid, MovieAffinity> affinities, HashSet<Guid> claimed, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            // "Hidden Gems" = high acclaim AND unfamiliar to the user (subcategories
            // the user does NOT already watch a lot) AND genuinely obscure (low TMDB
            // popularity). This is the opposite of the familiar "For You" — it surfaces
            // quality films outside the comfort zone.
            //
            // v1.5.21: "hidden" also requires obscurity, not just genre-unfamiliarity.
            // Acclaimed blockbusters (e.g. Seven Samurai, Black Panther) used to qualify
            // because they sat in an unfamili使用的 subcategory. A log-scaled TMDB
            // popularity penalty now pushes famous films down so obscure-acclaimed films
            // rise to the top. The penalty is skipped when popularity is unknown (no TMDB
            // key) or when FamePenaltyWeight is 0, restoring the old behavior.
            var familiarSubs = TopSubcategories(profile, 5); // most-watched subcats
            var fameScale = Math.Log(1 + 100); // popularity of ~100 → full penalty
            var gems = unwatched
                .Where(m => !claimed.Contains(m.ItemId))
                .Where(m => m.CriticalAcclaimScore >= 7)
                .Where(m => !SharesAnySubcategory(m, familiarSubs)) // unfamiliar = hidden
                .Select(m => new
                {
                    M = m,
                    FamePenalty = (_config.FamePenaltyWeight > 0 && m.Popularity > 0)
                        ? _config.FamePenaltyWeight * Math.Min(1.0, Math.Log(1 + m.Popularity) / fameScale)
                        : 0.0,
                    Score = m.CriticalAcclaimScore / 10.0
                            + Clamp(GetEffectiveAffinity(userId, affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoost(m, now)
                            + GetSoftPenalty(affinities, m.ItemId, now)
                })
                .OrderByDescending(x => x.Score - x.FamePenalty)
                .Take(15)
                .Select(x => x.M.ItemId)
                .ToList();

            await CreateOrUpdateJellyfinPlaylistAsync(userId, "Hidden Gems", gems, cancellationToken);
            return gems;
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

        private async Task<List<Guid>> GenerateSubcategoryPlaylistsAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, Dictionary<Guid, MovieAffinity> affinities, HashSet<Guid> claimed, CancellationToken cancellationToken)
        {
            var picks = new List<Guid>();
            if (profile.SubcategoryPreferences.Any() && _config.EnableSubcategory)
            {
                // Pick top familiar subcategory
                var topSubcategory = profile.SubcategoryPreferences.OrderByDescending(x => x.Value).First().Key;
                
                var familiarPicks = unwatched
                    .Where(m => !claimed.Contains(m.ItemId))
                    .Where(m => !string.IsNullOrEmpty(m.Subcategories) && m.Subcategories.Contains(topSubcategory, StringComparison.OrdinalIgnoreCase))
                    .Take(15)
                    .Select(m => m.ItemId)
                    .ToList();
                    
                if (familiarPicks.Any())
                    await CreateOrUpdateJellyfinPlaylistAsync(userId, $"{topSubcategory} For You", familiarPicks, cancellationToken);
                picks.AddRange(familiarPicks);
            }
            
            if (_config.EnableDiscover)
            {
                // Discover = the user's LEAST-explored subcategories (gateway into the unknown).
                // Surface movies from those subcats, ranked by acclaim + learned affinity +
                // new-movie nudge, so they're adjacent to taste, not random.
                var discovered = DiscoverPicks(userId, unwatched.Where(m => !claimed.Contains(m.ItemId)).ToList(), profile, affinities, 8);
                if (!discovered.Any())
                {
                    // Fallback: nothing in the least-familiar subcats is unwatched, so
                    // surface the top-acclaim unwatched films instead — Discover must
                    // always exist (CreateOrUpdateJellyfinPlaylistAsync skips empties).
                    discovered = unwatched
                        .Where(m => !claimed.Contains(m.ItemId) && m.IsClassified)
                        .OrderByDescending(m => m.CriticalAcclaimScore)
                        .Take(8)
                        .Select(m => m.ItemId)
                        .ToList();
                }
                if (discovered.Any())
                    await CreateOrUpdateJellyfinPlaylistAsync(userId, "Discover: Hidden World", discovered, cancellationToken);
                picks.AddRange(discovered);
            }
            return picks;
        }

        // Movies from the user's least-weighted subcategories, ranked by acclaim + affinity.
        private List<Guid> DiscoverPicks(Guid userId, List<MovieMetadata> unwatched, TasteProfile profile, Dictionary<Guid, MovieAffinity> affinities, int count)
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
                            + Clamp(GetEffectiveAffinity(userId, affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoost(m, now)
                })
                .OrderByDescending(x => x.Score)
                .Take(count)
                .Select(x => x.M.ItemId)
                .ToList();
        }

        private async Task<List<Guid>> GenerateWildCardPlaylistAsync(Guid userId, TasteProfile profile, List<MovieMetadata> unwatched, Dictionary<Guid, MovieAffinity> affinities, HashSet<Guid> claimed, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            // Wild Card = 100% exploration: the user's LEAST-explored subcategory,
            // only high-acclaim films, ranked by acclaim + affinity + new-movie nudge.
            var leastFamiliar = profile.SubcategoryPreferences.Any()
                ? profile.SubcategoryPreferences.OrderBy(kv => kv.Value).First().Key
                : null;

            var wildPicks = unwatched
                .Where(m => !claimed.Contains(m.ItemId))
                .Where(m => m.CriticalAcclaimScore >= 7)
                .Where(m => leastFamiliar == null || (m.Subcategories ?? "").Contains(leastFamiliar, StringComparison.OrdinalIgnoreCase))
                .Select(m => new
                {
                    M = m,
                    Score = m.CriticalAcclaimScore / 10.0
                            + Clamp(GetEffectiveAffinity(userId, affinities, m.ItemId) * _config.AffinityRankWeight, -_config.AffinityRankWeight, _config.AffinityRankWeight)
                            + GetNewMovieBoost(m, now)
                            + GetSoftPenalty(affinities, m.ItemId, now)
                })
                .OrderByDescending(x => x.Score)
                .Take(10)
                .Select(x => x.M.ItemId)
                .ToList();

            if (!wildPicks.Any())
            {
                // Fallback: relax to any high-acclaim unwatched film (no subcat match) so
                // Wild Card always exists when the strict pool is exhausted/claimed.
                wildPicks = unwatched
                    .Where(m => !claimed.Contains(m.ItemId) && m.IsClassified && m.CriticalAcclaimScore >= 7)
                    .OrderByDescending(m => m.CriticalAcclaimScore)
                    .Take(10)
                    .Select(m => m.ItemId)
                    .ToList();
            }

            await CreateOrUpdateJellyfinPlaylistAsync(userId, "Wild Card", wildPicks, cancellationToken);
            return wildPicks;
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

            // A watchlist is a TO-WATCH list: exclude films the user has already seen
            // (marked played anywhere), so the playlist reflects what's left to watch
            // rather than dumping every matched entry.
            var watchedIds = (await _watchHistoryService.GetWatchedMoviesAsync(userId, cancellationToken))
                .Select(w => w.ItemId).ToHashSet();
            var toWatch = matchedIds.Where(id => !watchedIds.Contains(id)).ToList();

            if (!toWatch.Any())
            {
                _logger.LogInformation("Watchlist for user {UserId} is fully watched; skipping 'From Your Watchlist'.", userId);
                return;
            }

            await CreateOrUpdateJellyfinPlaylistAsync(userId, "From Your Watchlist", toWatch, cancellationToken);
        }

        // v1.5.12: "Highly Rated by You" — the user's top-rated Letterboxd films that are
        // in the library, ordered by rating. Prefers unwatched films (a recommendation
        // list of things you'd love), then fills with watched ones if short.
        private async Task GenerateRatingsPlaylistAsync(Guid userId, Dictionary<Guid, double> ratings, CancellationToken cancellationToken)
        {
            var byId = (await _movieStore.GetAllMoviesAsync(cancellationToken)).ToDictionary(m => m.ItemId);
            var watched = (await _watchHistoryService.GetWatchedMoviesAsync(userId, cancellationToken)).Select(w => w.ItemId).ToHashSet();

            var rated = ratings
                .Where(kv => byId.ContainsKey(kv.Key))
                .Select(kv => new { Id = kv.Key, Rating = kv.Value, Watched = watched.Contains(kv.Key) })
                .OrderByDescending(x => x.Watched ? 0 : 1) // unwatched first
                .ThenByDescending(x => x.Rating)
                .Take(_config.MaxMoviesPerPlaylist)
                .Select(x => x.Id)
                .ToList();

            if (rated.Any())
                await CreateOrUpdateJellyfinPlaylistAsync(userId, "Highly Rated by You", rated, cancellationToken);
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

            // Director affinity: a small, configurable nudge if this movie shares a
            // director the user watches a lot (learned from watch history).
            double directorScore = 0.0;
            if (_config.DirectorAffinityBonus > 0 && !string.IsNullOrWhiteSpace(movie.Director) && profile.DirectorPreferences.Any())
            {
                foreach (var d in movie.Director.Split(','))
                {
                    var dir = d.Trim();
                    if (profile.DirectorPreferences.TryGetValue(dir, out double w))
                        directorScore = Math.Max(directorScore, w); // best director match
                }
            }

            // Subcategories are the strongest taste signal; moods refine it; director is a small bonus.
            // v1.5.14: keyword overlap (objective TMDB tags) is added as a configurable nudge.
            double keywordScore = 0.0;
            if (_config.KeywordWeight > 0 && !string.IsNullOrWhiteSpace(movie.Keywords) && profile.KeywordPreferences.Any())
            {
                try
                {
                    var movieKw = JsonSerializer.Deserialize<List<string>>(movie.Keywords)?
                        .Select(k => k.ToLowerInvariant()).ToHashSet() ?? new HashSet<string>();
                    var prefKw = profile.KeywordPreferences.Keys.Select(k => k.ToLowerInvariant()).ToHashSet();
                    if (movieKw.Count > 0 && prefKw.Count > 0)
                    {
                        double inter = movieKw.Intersect(prefKw).Count();
                        double uni = movieKw.Union(prefKw).Count();
                        if (uni > 0) keywordScore = Math.Min(1.0, inter / uni);
                    }
                }
                catch { /* ignore parse errors */ }
            }

            return 0.7 * subScore + 0.3 * moodScore
                   + Clamp(directorScore * _config.DirectorAffinityBonus, 0.0, _config.DirectorAffinityBonus)
                   + Clamp(keywordScore * _config.KeywordWeight, 0.0, _config.KeywordWeight);
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
        // v1.5.3: the half-life is scaled by the user's consumption-rate factor.
        private double GetEffectiveAffinity(Guid userId, Dictionary<Guid, MovieAffinity> affinities, Guid itemId)
        {
            if (!affinities.TryGetValue(itemId, out var row) || row == null) return 0.0;
            if (row.LastUpdated == null) return row.Affinity;
            if (!DateTime.TryParse(row.LastUpdated, out var updated)) return row.Affinity;
            var ageDays = (DateTime.UtcNow - updated).TotalDays;
            if (ageDays <= 0) return row.Affinity;
            var halfLife = _config.AffinityDecayHalfLifeDays * GetDecayFactor(userId);
            return row.Affinity * Math.Exp(-ageDays / Math.Max(1.0, halfLife));
        }

        // Soft penalty: while a movie's cooling window is active, it gets pushed DOWN
        // in ranking (graceful sink) instead of being hard-excluded. Strength 0 = full
        // ban, 1 = no penalty. Decays toward 0 as the window elapses.
        private double GetSoftPenalty(Dictionary<Guid, MovieAffinity> affinities, Guid itemId, DateTime now)
        {
            if (_config.SoftPenaltyStrength >= 1.0) return 0.0;
            if (!affinities.TryGetValue(itemId, out var row) || string.IsNullOrEmpty(row?.PenaltyUntil)) return 0.0;
            if (!DateTime.TryParse(row.PenaltyUntil, out var until)) return 0.0;
            if (until <= now) return 0.0;

            var totalWindow = until - now;              // remaining ban time
            if (totalWindow.TotalDays <= 0) return 0.0;
            // Fraction of the window still left (1 at ban start -> 0 at expiry).
            double frac = Clamp(totalWindow.TotalDays / Math.Max(1.0, _config.CoolingPeriodCycles * _config.PlaylistRefreshHours), 0.0, 1.0);
            return -frac * (1.0 - _config.SoftPenaltyStrength); // negative nudge
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

        // For You: a new movie only gets the recency boost if it actually fits the
        // user's taste (gated by NewMovieBoostMinFit) — so fresh additions surface
        // BECAUSE they fit, not merely because they are new (#4).
        private double GetNewMovieBoostByFit(MovieMetadata movie, TasteProfile profile, DateTime now)
        {
            if (_config.NewMovieBoostDays <= 0 || _config.NewMovieBoostMinFit <= 0) return GetNewMovieBoost(movie, now);
            var fit = ScoreMovieAgainstProfile(movie, profile);
            if (fit < _config.NewMovieBoostMinFit) return 0.0; // doesn't fit -> no boost
            return GetNewMovieBoost(movie, now);
        }

        // Novelty nudge (#6): movies not recently surfaced in playlists get a small
        // bonus that decays over NoveltyHalfLifeDays after they appear. Keeps the
        // same films from recycling to the top every refresh.
        private double GetNoveltyBonus(Guid userId, Dictionary<Guid, MovieAffinity> affinities, Guid itemId, DateTime now)
        {
            if (_config.NoveltyBonus <= 0 || _config.NoveltyHalfLifeDays <= 0) return 0.0;
            if (!affinities.TryGetValue(itemId, out var row) || row == null || string.IsNullOrEmpty(row.LastSurfaced))
                return _config.NoveltyBonus; // never surfaced -> full novelty nudge
            if (!DateTime.TryParse(row.LastSurfaced, out var surfaced)) return _config.NoveltyBonus;
            var ageDays = (now - surfaced).TotalDays;
            if (ageDays < 0) ageDays = 0;
            // Exponential decay: full nudge just after surfacing -> 0 after several half-lives.
            // v1.5.3: half-life scaled by the user's consumption-rate factor.
            var halfLife = _config.NoveltyHalfLifeDays * GetDecayFactor(userId);
            return _config.NoveltyBonus * Math.Exp(-ageDays / Math.Max(1.0, halfLife));
        }

        // v1.5.3: returns the consumption-rate decay multiplier for a user (default 1.0).
        // Faster watchers => <1 (quicker decay, fresher); slower => >1. Clamped 0.3x-3x.
        private double GetDecayFactor(Guid userId)
        {
            lock (_decayLock)
            {
                return _decayFactor.TryGetValue(userId, out var f) ? f : 1.0;
            }
        }

        // v1.5.3: compute the per-user decay factor from recent watch rate.
        // weeklyRate = movies watched in the last 8 weeks / 8. factor = clamp(rate / ref, 0.3, 3).
        private void ComputeDecayFactor(Guid userId, CancellationToken cancellationToken)
        {
            try
            {
                var watched = _watchHistoryService.GetWatchedMoviesWithDatesAsync(userId, cancellationToken).GetAwaiter().GetResult();
                var cutoff = DateTime.UtcNow.AddDays(-56);
                int recent = watched.Count(w => w.WatchedAt != null && w.WatchedAt.Value >= cutoff);
                double weeklyRate = recent / 8.0;
                double refRate = Math.Max(1.0, _config.DecayRateReferencePerWeek);
                double factor = weeklyRate / refRate;
                factor = factor < 0.3 ? 0.3 : (factor > 3.0 ? 3.0 : factor);
                _logger.LogInformation("User {UserId} decay factor {Factor} (weekly rate {Rate}, ref {Ref})", userId, factor, weeklyRate, refRate);
                lock (_decayLock)
                {
                    _decayFactor[userId] = factor;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compute decay factor for {UserId}; using 1.0", userId);
                lock (_decayLock)
                {
                    _decayFactor[userId] = 1.0;
                }
            }
        }

        // v1.5.4: snapshot the taste profile weekly (at most) so the config page can
        // show taste drift. Skips if a snapshot exists within the last 7 days.
        private async Task MaybeSaveTasteSnapshotAsync(Guid userId, TasteProfile profile, CancellationToken cancellationToken)
        {
            try
            {
                var latest = await _movieStore.GetLatestTasteSnapshotAsync(userId, cancellationToken);
                if (latest != null && (DateTime.UtcNow - latest.SnapshotAt).TotalDays < 7)
                    return;

                var snap = new TasteSnapshot
                {
                    UserId = userId.ToString(),
                    SnapshotAt = DateTime.UtcNow,
                    SubcategoryWeightsJson = JsonSerializer.Serialize(
                        (profile.SubcategoryPreferences ?? new Dictionary<string, double>())
                            .OrderByDescending(kv => kv.Value).Take(10)
                            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3))),
                    MoodWeightsJson = JsonSerializer.Serialize(
                        (profile.MoodPreferences ?? new Dictionary<string, double>())
                            .OrderByDescending(kv => kv.Value).Take(10)
                            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)))
                };
                await _movieStore.SaveTasteSnapshotAsync(snap, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save taste snapshot for {UserId}", userId);
            }
        }

        // Returns unwatched, classified movies + the user's affinity map. Penalized
        // movies are NOT excluded — they're softly pushed down in ranking instead.
        private async Task<(List<MovieMetadata> Movies, Dictionary<Guid, MovieAffinity> Affinities)>
            GetUnwatchedClassifiedMoviesAsync(Guid userId, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var affinities = await _movieStore.GetAffinitiesAsync(userId, cancellationToken);
            var watched = await _watchHistoryService.GetWatchedMoviesAsync(userId, cancellationToken);
            var watchedIds = watched.Select(m => m.ItemId).ToHashSet();

            var all = await _movieStore.GetAllMoviesAsync(cancellationToken);
            var result = new List<MovieMetadata>();
            var reasons = new Dictionary<Guid, List<string>>();
            foreach (var m in all)
            {
                if (watchedIds.Contains(m.ItemId))
                {
                    RecordExclusion(userId, m.ItemId, "Already watched");
                    continue;
                }
                if (!m.IsClassified)
                {
                    RecordExclusion(userId, m.ItemId, "Not yet AI-classified");
                    continue;
                }
                result.Add(m);
            }

            // Persist reasons for this refresh (other reasons, e.g. over diversity cap,
            // are appended later by ApplyDiversityCap during generation).
            lock (_exclusionsLock)
            {
                if (!_lastExclusions.TryGetValue(userId, out var existing) || existing == null)
                    _lastExclusions[userId] = reasons;
                else
                    foreach (var kv in reasons)
                        if (!existing.ContainsKey(kv.Key)) existing[kv.Key] = kv.Value;
            }

            // v1.5.32: permanent Info log (replaces temporary DIAG Warning logs from
            // v1.5.30/v1.5.31). This is the critical observability point: if all.Count
            // is 0 here, the DB read returned nothing and no playlists can be built.
            _logger.LogInformation(
                "Playlist input for {UserId}: {DbTotal} movies in DB, {Watched} watched, {Eligible} eligible (unwatched+classified).",
                userId, all.Count, watchedIds.Count, result.Count);

            return (result, affinities);
        }

        // v1.5.0: append an exclusion reason for a movie on the last refresh.
        private void RecordExclusion(Guid userId, Guid itemId, string reason)
        {
            lock (_exclusionsLock)
            {
                if (!_lastExclusions.TryGetValue(userId, out var dict) || dict == null)
                {
                    dict = new Dictionary<Guid, List<string>>();
                    _lastExclusions[userId] = dict;
                }
                if (!dict.TryGetValue(itemId, out var list))
                {
                    list = new List<string>();
                    dict[itemId] = list;
                }
                if (!list.Contains(reason)) list.Add(reason);
            }
        }

        // v1.5.0: read-only snapshot of "what the algorithm is doing right now" for a
        // user, for the config-page debug panel. Returns taste weights, active penalties
        // (with remaining cooling time), active novelty/new-movie boosts, and the last
        // refresh's exclusion reasons. No DB writes — pure observability.
        public async Task<object> GetDebugSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var profile = await _watchHistoryService.GetUserTasteProfileAsync(userId, cancellationToken);
            var affinities = await _movieStore.GetAffinitiesAsync(userId, cancellationToken);
            var all = await _movieStore.GetAllMoviesAsync(cancellationToken);
            var byId = all.ToDictionary(m => m.ItemId);

            var topSubcats = profile.SubcategoryPreferences
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3));

            var penalties = new List<object>();
            var boosts = new List<object>();
            foreach (var kv in affinities)
            {
                var row = kv.Value;
                if (row == null) continue;
                var title = byId.TryGetValue(kv.Key, out var mv) ? mv.Title : kv.Key.ToString();
                if (!string.IsNullOrEmpty(row.PenaltyUntil) &&
                    DateTime.TryParse(row.PenaltyUntil, out var until) && until > now)
                {
                    penalties.Add(new
                    {
                        ItemId = kv.Key,
                        Title = title,
                        Affinity = Math.Round(row.Affinity, 3),
                        CoolingHoursLeft = Math.Round((until - now).TotalHours, 1)
                    });
                }
                // Active novelty nudge (movie surfaced recently -> nudge decaying) or
                // currently-boosted new movie: surface anything with a non-trivial signal.
                if (!string.IsNullOrEmpty(row.LastSurfaced) &&
                    DateTime.TryParse(row.LastSurfaced, out var surfaced))
                {
                    var novelty = _config.NoveltyBonus * Math.Exp(-(now - surfaced).TotalDays / Math.Max(1, _config.NoveltyHalfLifeDays));
                    if (novelty > 0.001)
                        boosts.Add(new { ItemId = kv.Key, Title = title, Type = "novelty", Value = Math.Round(novelty, 4) });
                }
            }
            // Cap these lists: with large libraries the affinity table can be huge and an
            // uncapped response makes the config-page panel fail to render.
            var penaltyCount = penalties.Count;
            var boostCount = boosts.Count;
            penalties = penalties.Take(50).ToList();
            boosts = boosts.Take(50).ToList();

            Dictionary<Guid, List<string>> exclusions;
            lock (_exclusionsLock)
            {
                exclusions = _lastExclusions.TryGetValue(userId, out var e) && e != null
                    ? new Dictionary<Guid, List<string>>(e)
                    : new Dictionary<Guid, List<string>>();
            }
            var exclusionView = exclusions
                .Where(kv => byId.ContainsKey(kv.Key))
                .OrderBy(kv => byId[kv.Key].Title)
                .Take(50)
                .Select(kv => new { ItemId = kv.Key, Title = byId[kv.Key].Title, Reasons = kv.Value })
                .ToList();

            // v1.5.4: taste drift — compare the oldest stored snapshot to the current profile.
            var tasteDrift = await GetTasteDriftAsync(userId, profile, cancellationToken);

            // v1.5.5: recently surfaced history.
            var recent = await _movieStore.GetRecentSurfaceHistoryAsync(userId, 50, cancellationToken);
            var surfacedView = recent
                .Select(s => new
                {
                    ItemId = Guid.TryParse(s.ItemId, out var g) ? g : Guid.Empty,
                    Title = byId.TryGetValue(Guid.TryParse(s.ItemId, out var g2) ? g2 : Guid.Empty, out var mv) ? mv.Title : s.ItemId,
                    Playlist = s.PlaylistType,
                    SurfacedAt = s.SurfacedAt
                })
                .ToList();

            return new
            {
                GeneratedAt = now,
                HasTaste = profile.SubcategoryPreferences.Any() || profile.MoodPreferences.Any(),
                TopSubcategories = topSubcats,
                PenaltyCount = penaltyCount,
                ActivePenalties = penalties,
                BoostCount = boostCount,
                ActiveBoosts = boosts,
                ExclusionCount = exclusions.Count,
                Exclusions = exclusionView,
                TasteDrift = tasteDrift,
                RecentlySurfaced = surfacedView
            };
        }

        // v1.5.4: compute drift between the oldest stored taste snapshot and the current profile.
        private async Task<object> GetTasteDriftAsync(Guid userId, TasteProfile profile, CancellationToken cancellationToken)
        {
            try
            {
                var oldest = await _movieStore.GetOldestTasteSnapshotAsync(userId, cancellationToken);
                if (oldest == null)
                    return new { Available = false, Reason = "No snapshots yet (taken weekly on refresh)." };

                var prevSubs = JsonSerializer.Deserialize<Dictionary<string, double>>(oldest.SubcategoryWeightsJson) ?? new Dictionary<string, double>();
                var curSubs = profile.SubcategoryPreferences ?? new Dictionary<string, double>();
                var gained = curSubs.Keys.Where(k => !prevSubs.ContainsKey(k)).ToList();
                var lost = prevSubs.Keys.Where(k => !curSubs.ContainsKey(k)).ToList();
                var shifted = curSubs.Keys
                    .Where(k => prevSubs.ContainsKey(k) && Math.Abs(curSubs[k] - prevSubs[k]) > 0.05)
                    .OrderByDescending(k => Math.Abs(curSubs[k] - prevSubs[k]))
                    .Take(10)
                    .Select(k => new { Subcategory = k, From = Math.Round(prevSubs[k], 3), To = Math.Round(curSubs[k], 3), Delta = Math.Round(curSubs[k] - prevSubs[k], 3) })
                    .ToList();

                return new
                {
                    Available = true,
                    SnapshotAt = oldest.SnapshotAt,
                    Gained = gained,
                    Lost = lost,
                    Shifted = shifted
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute taste drift for {UserId}", userId);
                return new { Available = false, Reason = "Error computing drift." };
            }
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
                
                // Await creation so the refresh execution gate remains held until
                // Jellyfin has persisted the playlist. Otherwise another user's
                // cleanup can race the still-ownerless playlist creation task.
                var result = await _playlistManager.CreatePlaylist(req);
                _logger.LogInformation("Created playlist '{Name}' for user {UserId} with {Count} items (Result Id: {ResultId}).", name, userId, itemIds.Count, result.Id);

                // v1.5.39: Jellyfin 10.11 ignores PlaylistCreationRequest.UserId for
                // ownership — the created playlist has OwnerUserId == Guid.Empty, which
                // broke the owner-scoped find/delete above (ghost playlists accumulated
                // with '1'-suffixed paths). Stamp the owner explicitly and persist it.
                try
                {
                    // Creation is complete here, so the just-created ownerless playlist
                    // is visible before any other refresh can enter cleanup.
                    var created = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                            IsVirtualItem = false,
                            Recursive = true
                        }).OfType<Playlist>()
                        .Where(p => p.Name == name && p.OwnerUserId == Guid.Empty)
                        .OrderByDescending(p => p.DateCreated)
                        .FirstOrDefault();
                    if (created != null)
                    {
                        created.OwnerUserId = userId;
                        await created.UpdateToRepositoryAsync(MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stamp OwnerUserId on playlist '{Name}'.", name);
                }

                // Record surfacing for novelty tracking (so the same films don't recycle).
                try
                {
                    await _movieStore.MarkSurfacedAsync(userId, itemIds, cancellationToken);
                    await _movieStore.RecordSurfaceHistoryAsync(userId, itemIds, name, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record surfaced movies for playlist '{Name}'.", name);
                }
            }
            else
            {
                _logger.LogInformation("Skipped creating playlist '{Name}' because there were no items.", name);
            }
        }
    }
}
