using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    // Per-user Letterboxd rating: a film (matched to a library ItemId) and the
    // user's 0-5 star rating scraped from their public Letterboxd ratings page.
    // Drives the dominant "ratings" weight in recommendation scoring.
    public class UserRating
    {
        public Guid UserId { get; set; }
        public Guid ItemId { get; set; }   // matched Jellyfin library ItemId
        public double Rating { get; set; } // 0.5 .. 5.0 (Letterboxd half-star granularity)
        public string? SourceTitle { get; set; } // original title from Letterboxd (for debugging)
        public DateTime LastUpdated { get; set; }
    }
}
