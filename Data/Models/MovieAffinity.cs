using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    /// <summary>
    /// Per-(user, movie) learned rating ("affinity"). Written ONLY from watch
    /// events (punishment of siblings + reward of similar movies). Refresh reads
    /// it (with lazy time-decay) to nudge ranking. PenaltyUntil implements the
    /// temporary ban / cooling period for rejected movies.
    /// </summary>
    public class MovieAffinity
    {
        public string UserId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;

        /// <summary>Learned score, can be negative. Range roughly [-1, 1].</summary>
        public double Affinity { get; set; } = 0.0;

        /// <summary>ISO datetime until which the movie is excluded from recommendations.
        /// Null = no active ban.</summary>
        public string? PenaltyUntil { get; set; }

        public string LastUpdated { get; set; } = DateTime.UtcNow.ToString("o");
    }
}
