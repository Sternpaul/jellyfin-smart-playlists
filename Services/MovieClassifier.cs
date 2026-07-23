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
                // Different providers might wrap the response slightly differently based on their JSON mode.
                // We'll parse dynamically to find the array of movies.
                using var document = JsonDocument.Parse(jsonResponse);
                
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

                foreach (var element in moviesArray.EnumerateArray())
                {
                    if (!element.TryGetProperty("ItemId", out var idProp)) continue;
                    
                    var idString = idProp.GetString();
                    if (!Guid.TryParse(idString, out var itemId)) continue;

                    var movie = batch.FirstOrDefault(m => m.ItemId == itemId);
                    if (movie == null) continue;

                    movie.Subcategories = GetStringArray(element, "Subcategories");
                    movie.Moods = GetStringArray(element, "Moods");
                    movie.Themes = GetStringArray(element, "Themes");
                    
                    if (element.TryGetProperty("NarrativeStyle", out var ns)) movie.NarrativeStyle = ns.GetString();
                    if (element.TryGetProperty("Accessibility", out var acc)) movie.Accessibility = acc.GetString();
                    if (element.TryGetProperty("Intensity", out var inten)) movie.Intensity = inten.GetString();
                    
                    if (element.TryGetProperty("CriticalAcclaimScore", out var score)) 
                    {
                        if (score.TryGetInt32(out int val)) movie.CriticalAcclaimScore = val;
                    }

                    movie.IsClassified = true;
                    movie.LastUpdated = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AI classification JSON: {Json}", jsonResponse);
            }
        }

        private string GetStringArray(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                var values = prop.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                    
                return JsonSerializer.Serialize(values);
            }
            return "[]";
        }
    }
}
