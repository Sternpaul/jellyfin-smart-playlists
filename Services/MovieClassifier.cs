using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class MovieClassifier
    {
        private readonly MovieStore _movieStore;
        private readonly AIProviderFactory _aiProviderFactory;
        private readonly ILogger<MovieClassifier> _logger;

        private const int BatchSize = 20;

        public MovieClassifier(
            MovieStore movieStore,
            AIProviderFactory aiProviderFactory,
            ILogger<MovieClassifier> logger)
        {
            _movieStore = movieStore;
            _aiProviderFactory = aiProviderFactory;
            _logger = logger;
        }

        public async Task ClassifyPendingMoviesAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Checking for unclassified movies...");
            
            var unclassified = await _movieStore.GetUnclassifiedMoviesAsync(cancellationToken);
            if (!unclassified.Any())
            {
                _logger.LogInformation("No movies require classification.");
                return;
            }

            _logger.LogInformation("Found {Count} unclassified movies. Starting batch classification...", unclassified.Count);
            
            var provider = _aiProviderFactory.GetProvider();
            
            // Process in batches
            for (int i = 0; i < unclassified.Count; i += BatchSize)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var batch = unclassified.Skip(i).Take(BatchSize).ToList();
                _logger.LogInformation("Classifying batch {Start} to {End} of {Total}...", i + 1, i + batch.Count, unclassified.Count);

                try
                {
                    var jsonResponse = await provider.ClassifyMoviesAsync(batch, cancellationToken);
                    _logger.LogInformation("Raw AI response (first 2000 chars): {Response}", jsonResponse?.Length > 2000 ? jsonResponse.Substring(0, 2000) : jsonResponse);
                    ProcessClassificationResult(jsonResponse, batch);
                    
                    // Save batch to DB
                    await _movieStore.SaveMoviesAsync(batch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to classify batch. Skipping.");
                }
            }
            
            _logger.LogInformation("Classification complete.");
        }

        private void ProcessClassificationResult(string jsonResponse, List<MovieMetadata> batch)
        {
            try
            {
                var jsonToParse = jsonResponse.Trim();
                
                // Sometimes the LLM wraps it in ```json ... ```
                var match = System.Text.RegularExpressions.Regex.Match(jsonToParse, @"```(?:json)?\s*([\s\S]*?)\s*```");
                if (match.Success)
                {
                    jsonToParse = match.Groups[1].Value.Trim();
                }
                else
                {
                    // Fallback to searching for the first [ or { and the last ] or }
                    int startIndex = jsonToParse.IndexOfAny(new[] { '[', '{' });
                    int endIndex = jsonToParse.LastIndexOfAny(new[] { ']', '}' });
                    if (startIndex >= 0 && endIndex > startIndex)
                    {
                        jsonToParse = jsonToParse.Substring(startIndex, endIndex - startIndex + 1);
                    }
                }

                // Different providers might wrap the response slightly differently based on their JSON mode.
                // We'll parse dynamically to find the array of movies.
                using var document = JsonDocument.Parse(jsonToParse);
                
                JsonElement moviesArray;
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    moviesArray = document.RootElement;
                }
                else if (document.RootElement.TryGetProperty("movies", out var propArray))
                {
                    moviesArray = propArray;
                }
                else
                {
                    _logger.LogWarning("Could not find movies array in JSON response.");
                    return;
                }

                int index = 0;
                foreach (var element in moviesArray.EnumerateArray())
                {
                    MovieMetadata movie = null;

                    // 1. Try matching by ItemId or Id property
                    if (TryGetPropertyIgnoreCase(element, "ItemId", out var idProp) || TryGetPropertyIgnoreCase(element, "id", out idProp))
                    {
                        var idString = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.ToString();
                        if (Guid.TryParse(idString, out var itemId))
                        {
                            movie = batch.FirstOrDefault(m => m.ItemId == itemId);
                        }
                    }

                    // 2. Fallback to Title matching if ItemId didn't match
                    if (movie == null && (TryGetPropertyIgnoreCase(element, "Title", out var titleProp) || TryGetPropertyIgnoreCase(element, "title", out titleProp)))
                    {
                        var titleStr = titleProp.GetString();
                        if (!string.IsNullOrWhiteSpace(titleStr))
                        {
                            movie = batch.FirstOrDefault(m => m.Title.Equals(titleStr, StringComparison.OrdinalIgnoreCase) || m.Title.StartsWith(titleStr, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    // 3. Fallback to array index
                    if (movie == null && index < batch.Count)
                    {
                        movie = batch[index];
                    }

                    if (movie == null)
                    {
                        index++;
                        continue;
                    }

                    var subcats = GetStringArray(element, "Subcategories");
                    var moods = GetStringArray(element, "Moods");
                    var themes = GetStringArray(element, "Themes");
                    var narrativeStyle = GetStringProperty(element, "NarrativeStyle");
                    var accessibility = GetStringProperty(element, "Accessibility");
                    var intensity = GetStringProperty(element, "Intensity");
                    var score = GetIntProperty(element, "CriticalAcclaimScore");

                    movie.Subcategories = subcats;
                    movie.Moods = moods;
                    movie.Themes = themes;
                    if (!string.IsNullOrEmpty(narrativeStyle)) movie.NarrativeStyle = narrativeStyle;
                    if (!string.IsNullOrEmpty(accessibility)) movie.Accessibility = accessibility;
                    if (!string.IsNullOrEmpty(intensity)) movie.Intensity = intensity;
                    if (score > 0) movie.CriticalAcclaimScore = score;

                    movie.IsClassified = true;
                    movie.LastUpdated = DateTime.UtcNow;
                    index++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse JSON, attempting fallback text parsing. Error: {Msg}", ex.Message);
                
                var successCount = 0;
                var lines = jsonResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\*\s*\*(?<title>[^*]+)\*:\s*(?<subcategories>[^.]+)\.\s*Moods:\s*(?<moods>[^.]+)\.\s*Themes:\s*(?<themes>[^.]+)\.\s*Style:\s*(?<style>[^.]+)\.\s*Accessibility:\s*(?<acc>[^.]+)\.\s*Intensity:\s*(?<int>[^.]+)\.\s*Score:\s*(?<score>\d+)");
                    if (match.Success)
                    {
                        var title = match.Groups["title"].Value.Trim();
                        var movie = batch.FirstOrDefault(m => m.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase) || m.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                        if (movie != null)
                        {
                            movie.Subcategories = JsonSerializer.Serialize(match.Groups["subcategories"].Value.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                            movie.Moods = JsonSerializer.Serialize(match.Groups["moods"].Value.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                            movie.Themes = JsonSerializer.Serialize(match.Groups["themes"].Value.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                            movie.NarrativeStyle = match.Groups["style"].Value.Trim();
                            movie.Accessibility = match.Groups["acc"].Value.Trim();
                            movie.Intensity = match.Groups["int"].Value.Trim();
                            if (int.TryParse(match.Groups["score"].Value, out int scoreVal)) movie.CriticalAcclaimScore = scoreVal;
                            
                            movie.IsClassified = true;
                            movie.LastUpdated = DateTime.UtcNow;
                            successCount++;
                        }
                    }
                }
                
                if (successCount == 0)
                {
                    _logger.LogError(ex, "Error parsing AI classification JSON and fallback failed: {Json}", jsonResponse);
                }
                else
                {
                    _logger.LogInformation("Successfully parsed {Count} movies using text fallback.", successCount);
                }
            }
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        private string GetStringArray(JsonElement element, string propertyName)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    var values = prop.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                        
                    return JsonSerializer.Serialize(values);
                }
                else if (prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        var parts = val.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                        return JsonSerializer.Serialize(parts);
                    }
                }
            }
            return "[]";
        }

        private string? GetStringProperty(JsonElement element, string propertyName)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
                if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined) return prop.ToString();
            }
            return null;
        }

        private int GetIntProperty(JsonElement element, string propertyName, int defaultValue = 0)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val)) return val;
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int strVal)) return strVal;
            }
            return defaultValue;
        }
    }
}
