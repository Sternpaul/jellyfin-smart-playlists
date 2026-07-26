using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    public enum ManagedPlaylistKind
    {
        RotatingRecommendation = 0,
        PersistentCollection = 1
    }

    public class ManagedPlaylist
    {
        public Guid UserId { get; set; }
        public string LogicalKey { get; set; } = string.Empty;
        public Guid PlaylistId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public ManagedPlaylistKind Kind { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
