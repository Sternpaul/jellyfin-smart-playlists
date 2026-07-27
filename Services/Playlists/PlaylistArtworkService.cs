using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public sealed class PlaylistArtworkService
{
    private const string ResourcePrefix = "Jellyfin.Plugin.AIRecommender.Assets.Playlists";
    private readonly IProviderManager _providerManager;
    private readonly ILogger<PlaylistArtworkService> _logger;

    public PlaylistArtworkService(IProviderManager providerManager, ILogger<PlaylistArtworkService> logger)
    {
        _providerManager = providerManager;
        _logger = logger;
    }

    public async Task ApplyIfMissingAsync(
        Playlist playlist,
        string displayName,
        Guid? anchorItemId,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        var changed = false;
        if (ShouldWriteImage(playlist.HasImage(ImageType.Primary, 0)))
        {
            changed |= await TryCopyAnchorImageAsync(playlist, anchorItemId, ImageType.Primary, libraryManager, cancellationToken)
                || await SaveEmbeddedAsync(playlist, displayName, "primary", ImageType.Primary, cancellationToken);
        }

        if (ShouldWriteImage(playlist.HasImage(ImageType.Backdrop, 0)))
        {
            changed |= await TryCopyAnchorImageAsync(playlist, anchorItemId, ImageType.Backdrop, libraryManager, cancellationToken)
                || await SaveEmbeddedAsync(playlist, displayName, "backdrop", ImageType.Backdrop, cancellationToken);
        }

        if (changed)
        {
            playlist.OnMetadataChanged();
            await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken);
        }
    }

    public static bool ShouldWriteImage(bool hasExistingImage) => !hasExistingImage;

    public static string GetAssetKey(string displayName)
    {
        var name = displayName.Trim();
        if (name.StartsWith("Because You Watched ", StringComparison.OrdinalIgnoreCase)) return "because-you-watched";
        if (name.Equals("For You", StringComparison.OrdinalIgnoreCase)) return "for-you";
        if (name.Equals("Hidden Gems", StringComparison.OrdinalIgnoreCase)) return "hidden-gems";
        if (name.Equals("Recently Added", StringComparison.OrdinalIgnoreCase)) return "recently-added";
        if (name.Equals("Discover: Hidden World", StringComparison.OrdinalIgnoreCase)) return "discover";
        if (name.Equals("Wild Card", StringComparison.OrdinalIgnoreCase)) return "wild-card";
        if (name.Equals("From Your Watchlist", StringComparison.OrdinalIgnoreCase)) return "watchlist";
        if (name.Equals("Highly Rated by You", StringComparison.OrdinalIgnoreCase)) return "highly-rated";
        return "subcategory";
    }

    private async Task<bool> TryCopyAnchorImageAsync(
        Playlist playlist,
        Guid? anchorItemId,
        ImageType imageType,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        if (!anchorItemId.HasValue || libraryManager.GetItemById(anchorItemId.Value) is not BaseItem anchor)
            return false;

        var image = anchor.GetImageInfo(imageType, 0);
        if (image == null || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
            return false;

        try
        {
            await using var stream = File.OpenRead(image.Path);
            await _providerManager.SaveImage(
                playlist,
                stream,
                MimeTypeForPath(image.Path),
                imageType,
                0,
                cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy {ImageType} artwork from anchor {AnchorId}; using embedded fallback.", imageType, anchorItemId);
            return false;
        }
    }

    private async Task<bool> SaveEmbeddedAsync(
        Playlist playlist,
        string displayName,
        string shape,
        ImageType imageType,
        CancellationToken cancellationToken)
    {
        var resourceName = $"{ResourcePrefix}.{GetAssetKey(displayName)}-{shape}.png";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded playlist artwork resource '{resourceName}'.");
        await _providerManager.SaveImage(playlist, stream, "image/png", imageType, 0, cancellationToken);
        return true;
    }

    private static string MimeTypeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };
}
