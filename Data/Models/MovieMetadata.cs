using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    public class MovieMetadata
    {
        public Guid ItemId { get; set; } // Jellyfin's internal BaseItem ID
        
        // Basic metadata
        public string Title { get; set; } = string.Empty;
        public int? ReleaseYear { get; set; }
        public string? ImdbId { get; set; }
        public int? TmdbId { get; set; }   // v1.5.14: TMDB movie id (ProviderIds["Tmdb"]) — used to fetch keywords
        public string? Plot { get; set; }
        public string? Director { get; set; }
        public string? Cast { get; set; }
        
        // AI Assigned Metadata
        public string? Subcategories { get; set; } // JSON array of strings
        public string? Moods { get; set; } // JSON array of strings
        public string? Themes { get; set; } // JSON array of strings
        public string? NarrativeStyle { get; set; }
        public string? Accessibility { get; set; }
        public string? Intensity { get; set; }
        public int CriticalAcclaimScore { get; set; } = 0; // 1-10
        public string? Keywords { get; set; } // v1.5.14: JSON array of TMDB keyword strings (precision signal)
        public bool IsClassified { get; set; } = false;
        public DateTime DateAdded { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
