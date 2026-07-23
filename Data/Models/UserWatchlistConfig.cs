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
        
        public DateTime LastSynced { get; set; }
        public string? MatchedItemIds { get; set; } // JSON array of Guid
    }
}
