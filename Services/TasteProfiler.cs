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
        public TasteProfile CalculateProfile(Guid userId, List<MovieMetadata> watchedMovies)
        {
            var profile = new TasteProfile { UserId = userId };
            
            if (!watchedMovies.Any()) return profile;

            // Calculate Subcategory weights
            var subcatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var moodCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var movie in watchedMovies)
            {
                if (!string.IsNullOrWhiteSpace(movie.Subcategories))
                {
                    try
                    {
                        var subs = System.Text.Json.JsonSerializer.Deserialize<List<string>>(movie.Subcategories);
                        if (subs != null)
                        {
                            foreach (var s in subs)
                            {
                                if (subcatCounts.ContainsKey(s)) subcatCounts[s]++;
                                else subcatCounts[s] = 1;
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
                                if (moodCounts.ContainsKey(m)) moodCounts[m]++;
                                else moodCounts[m] = 1;
                            }
                        }
                    }
                    catch { /* Ignore parse errors */ }
                }
            }

            // Normalize weights
            if (subcatCounts.Any())
            {
                int maxSubCount = subcatCounts.Values.Max();
                foreach (var kvp in subcatCounts)
                {
                    profile.SubcategoryPreferences[kvp.Key] = (double)kvp.Value / maxSubCount;
                }
            }

            if (moodCounts.Any())
            {
                int maxMoodCount = moodCounts.Values.Max();
                foreach (var kvp in moodCounts)
                {
                    profile.MoodPreferences[kvp.Key] = (double)kvp.Value / maxMoodCount;
                }
            }

            return profile;
        }
    }
}
