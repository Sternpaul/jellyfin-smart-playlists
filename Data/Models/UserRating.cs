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
        public double Rating { get; set; } // Greater than 0 and at most 5.
        public string? SourceTitle { get; set; } // original title from Letterboxd (for debugging)
        public DateTime LastUpdated { get; set; }
    }
}
