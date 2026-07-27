using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services.Collections;

public static class PersistentCollectionPolicy
{
    public const int MaximumMembers = 100;
    public const string LogicalKeyPrefix = "collection:";

    public static string LogicalKey(Guid definitionId)
    {
        if (definitionId == Guid.Empty)
            throw new ArgumentException("A persistent collection definition ID is required.", nameof(definitionId));
        return LogicalKeyPrefix + definitionId.ToString("N");
    }

    public static bool IsOwnedCollectionRegistration(ManagedPlaylist registration) =>
        registration.Kind == ManagedPlaylistKind.PersistentCollection &&
        registration.LogicalKey.StartsWith(LogicalKeyPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldUsePlaylistForLearning(
        Guid ownerUserId,
        Guid playlistId,
        Guid watchedUserId,
        IReadOnlySet<Guid> excludedPersistentPlaylistIds) =>
        ownerUserId == watchedUserId && !excludedPersistentPlaylistIds.Contains(playlistId);

}
