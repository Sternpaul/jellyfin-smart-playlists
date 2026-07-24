using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    // v1.5.5: persistent log of which movies were surfaced to a user, in which
    // playlist, and when. Powers the "recently surfaced" view and gives novelty
    // tracking a real, queryable surface history.
    public class SurfaceHistory
    {
        [Key]
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public string PlaylistType { get; set; } = string.Empty;
        public DateTime SurfacedAt { get; set; }
    }
}
