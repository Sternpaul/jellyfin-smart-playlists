using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    // v1.5.4: periodic snapshot of a user's taste profile, so the config page can
    // show how tastes DRIFT over time. Stored as JSON to avoid a wide, churny schema.
    public class TasteSnapshot
    {
        [Key]
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public DateTime SnapshotAt { get; set; }
        public string SubcategoryWeightsJson { get; set; } = "{}";
        public string MoodWeightsJson { get; set; } = "{}";
    }
}
