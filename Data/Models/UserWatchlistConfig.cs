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

        // v1.5.17: per-user Letterboxd ratings ingestion. Instead of scraping
        // Letterboxd's HTML (fragile, ToS-gray), the user points this at a JSON
        // export of their ratings (e.g. the raw GitHub URL of their own
        // letterboxd-lists/public/ratings.json). Each entry carries an imdb_id and a
        // 0.5-5.0 rating, so matching to the library is exact. Blank = no ratings weight.
        public string? RatingsJsonUrl { get; set; }
        public bool EnableRatingsPlaylist { get; set; } = false;

        public DateTime LastSynced { get; set; }
        public string? MatchedItemIds { get; set; } // JSON array of Guid
    }
}
