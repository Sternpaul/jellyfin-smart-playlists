using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class TasteProfile
    {
        public Guid UserId { get; set; }

        // Key: Subcategory, Value: Weight (0.0 to 1.0)
        public Dictionary<string, double> SubcategoryPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Key: Mood, Value: Weight (0.0 to 1.0)
        public Dictionary<string, double> MoodPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class TasteProfiler
    {
        // Exponential time-decay: a movie watched `halfLifeDays` ago contributes
        // half the weight of one watched today. Recent viewing shapes taste more
        // strongly, but older history is never fully discarded.
        public TasteProfile CalculateProfile(Guid userId, List<(MovieMetadata Movie, DateTime? WatchedAt)> watchedMovies, double halfLifeDays)
        {
            var profile = new TasteProfile { UserId = userId };

            if (!watchedMovies.Any()) return profile;

            var now = DateTime.UtcNow;
            var subcatWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var moodWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var (movie, watchedAt) in watchedMovies)
            {
                // Decay weight for this watch event. No timestamp -> treat as full weight.
                double decay = 1.0;
                if (watchedAt.HasValue)
                {
                    var ageDays = (now - watchedAt.Value).TotalDays;
                    if (ageDays < 0) ageDays = 0; // clock skew guard
                    decay = Math.Exp(-ageDays / Math.Max(1.0, halfLifeDays));
                }

                if (!string.IsNullOrWhiteSpace(movie.Subcategories))
                {
                    try
                    {
                        var subs = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.Subcategories);
                        if (subs != null)
                        {
                            foreach (var s in subs)
                            {
                                if (subcatWeights.ContainsKey(s)) subcatWeights[s] += decay;
                                else subcatWeights[s] = decay;
                            }
                        }
                    }
                    catch { /* Ignore parse errors */ }
                }

                if (!string.IsNullOrWhiteSpace(movie.Moods))
                {
                    try
                    {
                        var moods = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.Moods);
                        if (moods != null)
                        {
                            foreach (var m in moods)
                            {
                                if (moodWeights.ContainsKey(m)) moodWeights[m] += decay;
                                else moodWeights[m] = decay;
                            }
                        }
                    }
                    catch { /* Ignore parse errors */ }
                }
            }

            // Normalize so the strongest signal maps to 1.0 (keeps ScoreMovieAgainstProfile unchanged).
            if (subcatWeights.Any())
            {
                double max = subcatWeights.Values.Max();
                foreach (var kvp in subcatWeights)
                    profile.SubcategoryPreferences[kvp.Key] = kvp.Value / max;
            }

            if (moodWeights.Any())
            {
                double max = moodWeights.Values.Max();
                foreach (var kvp in moodWeights)
                    profile.MoodPreferences[kvp.Key] = kvp.Value / max;
            }

            return profile;
        }
    }
}
