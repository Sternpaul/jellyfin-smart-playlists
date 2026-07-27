using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public sealed record RepresentativeArtworkSource(
    Guid ItemId,
    string Path,
    ManagedArtworkSourceImageType SourceType);

public static class RepresentativeArtworkSelector
{
    public static IReadOnlyList<Guid> RankFinalMembers(
        IEnumerable<Guid> rankedItemIds,
        IEnumerable<Guid> finalPlaylistMembers)
    {
        ArgumentNullException.ThrowIfNull(rankedItemIds);
        ArgumentNullException.ThrowIfNull(finalPlaylistMembers);

        var final = finalPlaylistMembers.Where(id => id != Guid.Empty).Distinct().ToList();
        var finalSet = final.ToHashSet();
        return rankedItemIds
            .Where(finalSet.Contains)
            .Concat(final)
            .Distinct()
            .ToList();
    }

    public static Guid? Select(
        Guid? anchorItemId,
        IEnumerable<Guid> rankedItemIds,
        Func<Guid, bool> hasUsableLocalArtwork)
    {
        ArgumentNullException.ThrowIfNull(rankedItemIds);
        ArgumentNullException.ThrowIfNull(hasUsableLocalArtwork);

        if (anchorItemId is Guid anchor && anchor != Guid.Empty && hasUsableLocalArtwork(anchor))
            return anchor;

        foreach (var itemId in rankedItemIds)
        {
            if (itemId != Guid.Empty && hasUsableLocalArtwork(itemId))
                return itemId;
        }

        return null;
    }

    public static RepresentativeArtworkSource? SelectSource(
        Guid? anchorItemId,
        IEnumerable<Guid> rankedItemIds,
        Func<Guid, string?> backdropPath,
        Func<Guid, string?> primaryPath)
    {
        ArgumentNullException.ThrowIfNull(rankedItemIds);
        ArgumentNullException.ThrowIfNull(backdropPath);
        ArgumentNullException.ThrowIfNull(primaryPath);

        var candidates = anchorItemId is Guid anchor && anchor != Guid.Empty
            ? new[] { anchor }.Concat(rankedItemIds)
            : rankedItemIds;
        foreach (var itemId in candidates.Where(id => id != Guid.Empty).Distinct())
        {
            var backdrop = backdropPath(itemId);
            if (!string.IsNullOrWhiteSpace(backdrop))
                return new RepresentativeArtworkSource(itemId, backdrop, ManagedArtworkSourceImageType.Backdrop);

            var primary = primaryPath(itemId);
            if (!string.IsNullOrWhiteSpace(primary))
                return new RepresentativeArtworkSource(itemId, primary, ManagedArtworkSourceImageType.Primary);
        }

        return null;
    }
}
