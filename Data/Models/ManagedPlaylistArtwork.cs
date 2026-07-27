using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    public enum ManagedArtworkImageType
    {
        Primary = 0,
        Backdrop = 1
    }

    public enum ManagedArtworkSourceImageType
    {
        Backdrop = 0,
        Primary = 1,
        EmbeddedFallback = 2
    }

    public class ManagedPlaylistArtwork
    {
        public Guid PlaylistId { get; set; }
        public ManagedArtworkImageType ImageType { get; set; }
        public string GeneratedHash { get; set; } = string.Empty;
        public Guid? SourceItemId { get; set; }
        public ManagedArtworkSourceImageType SourceImageType { get; set; }
        public string SourceHash { get; set; } = string.Empty;
        public string RenderedTitle { get; set; } = string.Empty;
        public int TemplateVersion { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
