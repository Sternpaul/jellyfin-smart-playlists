using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    public enum WatchlistImportMethod
    {
        None,
        JsonUrl,
        CsvUpload
    }

    public class UserWatchlistConfig
    {
        public Guid UserId { get; set; } // Jellyfin's internal User ID
        
        public WatchlistImportMethod ImportMethod { get; set; } = WatchlistImportMethod.None;
        
        public string? JsonUrl { get; set; }
        public string? CsvData { get; set; } // Raw CSV content
        
        public bool EnableWatchlistPlaylist { get; set; } = false;

        // v1.5.12: per-user Letterboxd ratings ingestion (scraped from the public
        // ratings page). RatingsUsername is the Letterboxd handle; the plugin scrapes
        // letterboxd.com/{username}/films/ratings/ and matches films to the library.
        public string? RatingsUsername { get; set; }
        public bool EnableRatingsPlaylist { get; set; } = false;

        public DateTime LastSynced { get; set; }
        public string? MatchedItemIds { get; set; } // JSON array of Guid
    }
}
