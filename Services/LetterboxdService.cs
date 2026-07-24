using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class LetterboxdEntry
    {
        public string? imdb_id { get; set; }
        public string? title { get; set; }
        public string? release_year { get; set; }
    }

    public class LetterboxdService
    {
        private readonly HttpClient _httpClient;
        private readonly MovieStore _movieStore;
        private readonly ILogger<LetterboxdService> _logger;

        public LetterboxdService(
            HttpClient httpClient,
            MovieStore movieStore,
            ILogger<LetterboxdService> logger)
        {
            _httpClient = httpClient;
            _movieStore = movieStore;
            _logger = logger;
        }

        public async Task SyncWatchlistAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var config = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (config == null)
                return;

            // v1.5.11: infer the import method from the stored data when it was saved as
            // None (configs saved before this fix), so syncing still works.
            if (config.ImportMethod == WatchlistImportMethod.None)
            {
                if (!string.IsNullOrWhiteSpace(config.JsonUrl))
                    config.ImportMethod = WatchlistImportMethod.JsonUrl;
                else if (!string.IsNullOrWhiteSpace(config.CsvData))
                    config.ImportMethod = WatchlistImportMethod.CsvUpload;
                else
                    return;
            }

            List<LetterboxdEntry> entries = new();

            try
            {
                if (config.ImportMethod == WatchlistImportMethod.JsonUrl && !string.IsNullOrWhiteSpace(config.JsonUrl))
                {
                    _logger.LogInformation("Syncing Letterboxd watchlist for User {UserId} from JSON URL.", userId);
                    entries = await FetchFromJsonUrlAsync(config.JsonUrl, cancellationToken);
                }
                else if (config.ImportMethod == WatchlistImportMethod.CsvUpload && !string.IsNullOrWhiteSpace(config.CsvData))
                {
                    _logger.LogInformation("Syncing Letterboxd watchlist for User {UserId} from CSV data.", userId);
                    entries = ParseCsv(config.CsvData);
                }

                if (entries.Any())
                {
                    var matchedIds = await MatchEntriesToLibraryAsync(entries, cancellationToken);
                    config.MatchedItemIds = JsonSerializer.Serialize(matchedIds);
                    config.LastSynced = DateTime.UtcNow;
                    await _movieStore.SaveUserWatchlistConfigAsync(config, cancellationToken);
                    
                    _logger.LogInformation("Successfully matched {Count} movies from Letterboxd to Jellyfin library.", matchedIds.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync Letterboxd watchlist for user {UserId}", userId);
            }
        }

        private async Task<List<LetterboxdEntry>> FetchFromJsonUrlAsync(string url, CancellationToken cancellationToken)
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var list = await response.Content.ReadFromJsonAsync<List<LetterboxdEntry>>(cancellationToken: cancellationToken);
            return list ?? new List<LetterboxdEntry>();
        }

        private List<LetterboxdEntry> ParseCsv(string csv)
        {
            // Simplified CSV parsing for Letterboxd exports.
            // Expected headers usually include Date, Name, Year, Letterboxd URI
            // Since we can't guarantee IMDB id in basic CSV export, we'll try to extract Name and Year.
            var entries = new List<LetterboxdEntry>();
            var lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= 1) return entries;
            
            var headers = lines[0].Split(',').Select(h => h.Trim('"').ToLowerInvariant()).ToList();
            int nameIdx = headers.IndexOf("name");
            int yearIdx = headers.IndexOf("year");

            if (nameIdx == -1) return entries;

            for (int i = 1; i < lines.Length; i++)
            {
                // Simple CSV split (doesn't handle commas inside quotes well, but good enough for MVP)
                var parts = lines[i].Split(','); 
                if (parts.Length > nameIdx)
                {
                    var entry = new LetterboxdEntry { title = parts[nameIdx].Trim('"') };
                    if (yearIdx != -1 && parts.Length > yearIdx)
                    {
                        entry.release_year = parts[yearIdx].Trim('"');
                    }
                    entries.Add(entry);
                }
            }

            return entries;
        }

        private async Task<List<Guid>> MatchEntriesToLibraryAsync(List<LetterboxdEntry> entries, CancellationToken cancellationToken)
        {
            var matched = new List<Guid>();
            var libraryMovies = await _movieStore.GetAllMoviesAsync(cancellationToken);

            // For faster lookup
            var imdbDict = libraryMovies
                .Where(m => !string.IsNullOrWhiteSpace(m.ImdbId))
                .ToDictionary(m => m.ImdbId!, StringComparer.OrdinalIgnoreCase);

            // Normalized-title index for lenient matching (case/whitespace/punctuation/year-insensitive).
            var titleDict = new Dictionary<string, List<MovieMetadata>>();
            foreach (var m in libraryMovies)
            {
                if (string.IsNullOrWhiteSpace(m.Title)) continue;
                var key = NormalizeTitle(m.Title);
                if (!titleDict.TryGetValue(key, out var list))
                    titleDict[key] = list = new List<MovieMetadata>();
                list.Add(m);
            }

            foreach (var entry in entries)
            {
                // 1. Exact IMDB match (highest priority)
                if (!string.IsNullOrWhiteSpace(entry.imdb_id) && imdbDict.TryGetValue(entry.imdb_id, out var movieById))
                {
                    matched.Add(movieById.ItemId);
                    continue;
                }

                // 2. Lenient Title (+ optional Year) match
                if (!string.IsNullOrWhiteSpace(entry.title))
                {
                    int.TryParse(entry.release_year, out int parsedYear);
                    var normTitle = NormalizeTitle(entry.title);

                    if (titleDict.TryGetValue(normTitle, out var candidates))
                    {
                        var movieByTitle = candidates.FirstOrDefault(m =>
                            parsedYear == 0 || !m.ReleaseYear.HasValue || Math.Abs(m.ReleaseYear.Value - parsedYear) <= 1);
                        if (movieByTitle == null && parsedYear != 0)
                            movieByTitle = candidates.First(); // year unknown in library: accept title match
                        if (movieByTitle != null)
                        {
                            matched.Add(movieByTitle.ItemId);
                            continue;
                        }
                    }

                    // 3. Substring fallback: watchlist title contained in a library title
                    // (or vice-versa), ignoring year suffixes. Catches "Alien" vs "Alien (1979)".
                    var baseTitle = StripYearSuffix(normTitle);
                    var fallback = libraryMovies.FirstOrDefault(m =>
                        !string.IsNullOrWhiteSpace(m.Title) &&
                        (StripYearSuffix(NormalizeTitle(m.Title)).Contains(baseTitle) ||
                         baseTitle.Contains(StripYearSuffix(NormalizeTitle(m.Title)))) &&
                        (parsedYear == 0 || !m.ReleaseYear.HasValue || Math.Abs(m.ReleaseYear.Value - parsedYear) <= 2));
                    if (fallback != null)
                        matched.Add(fallback.ItemId);
                }
            }

            return matched;
        }

        // Lowercase, trim, collapse whitespace, strip a trailing "(YYYY)" / "YYYY" and
        // common separators so "Spider-Man: Into the Spider-Verse" matches across sources.
        private static string NormalizeTitle(string title)
        {
            var t = title.ToLowerInvariant().Trim();
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*\(?\b(19|20)\d{2}\b\)?\s*$", ""); // trailing year
            t = t.Replace(":", "").Replace("-", " ").Replace("–", " ").Replace("&", "and");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
            return t;
        }

        private static string StripYearSuffix(string normalized)
        {
            // normalized already strips trailing year; just return as-is for the substring compare.
            return normalized;
        }
    }
}
