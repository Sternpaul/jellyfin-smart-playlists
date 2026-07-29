using Jellyfin.Data.Enums;
using MediaBrowser.Model.Playlists;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public static class ManagedPlaylistCreationPolicy
{
    public static PlaylistCreationRequest CreateEmptyVideoRequest(string name, Guid userId)
        => new()
        {
            Name = name,
            UserId = userId,
            ItemIdList = Array.Empty<Guid>(),
            MediaType = MediaType.Video,
            Public = false
        };
}
