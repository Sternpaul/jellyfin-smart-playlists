using System.Reflection;
using System.Security.Cryptography;
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
    private static readonly HashSet<string> LegacyGeneratedArtworkHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        "3a35bf202fe976eb27bb44e6bb4c41d6917443d522e63b1f9b808bcf08949b45",
        "7b50fe1ba1e8fb66b0daf21e84249949a4cca111ccbbe6f2d000c99602b04a1b",
        "768c62d0557a632b51f657378c62d522611717f6bd9cc4125b15c64ee879bf9f",
        "db975fee273d242bd4bcc966377307664d73abfb3ee30853a66051504ef552bf",
        "71a6f9e12efbf2b17e2444cd5f5f1c3178bbac9fa6b60a18cdaa5ea5d1794b71",
        "ab3c0e6a9086f3c4f09c238c8215bbb131c3dbb3439899576b509f23d12adaaa",
        "97c7a58c825cc72625d9b2d7246b98ab131904d18794f7a9ea58a24b43b89163",
        "b369b56bc65fe6a4544cf57e379abeed1f4ff61025932007ec0523105bd81036",
        "1e0ca816c82b053e6c9b65aab9601a0ce44ce52a37b3482fbfc306741f95a762",
        "83c1059855c0affd05845477d8db33215a1bfaad4fb4256be3155cbdfdc9abab",
        "2ee815397643932b3d49cc108ec19190364cb697197effd7ffb1a99169a095d6",
        "cca6f3d39f6c32ef99609e33c2f3e61c82ed42df4c27c959ae306205652f51f7",
        "be9fb484be0dbc106c497e9f79258a6b4a5509c9986735498efd1091ed2a9dbf",
        "1d4b554126e70d954354c9cb6546b097aeebae72698fd9f517c3dd7153a27e4d",
        "ee3c6c2070cc92be8911aa4b66c31a97392463e1ab31f521e251aee49e1fe728",
        "d3f16c6bac2ef29ff48ea1bb2da1c3be2992713c8c09cd915552c45f12c5987b",
        "4abe2f68731a502f572cb45ee63e8d1ec5402fe901cc01889c336f7854075f04",
        "4a64baeb288f95cfd6d097f60091c79e7f7ec5f7e67b2b0c95a27c43a594ef9b"
    };
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
        var primary = playlist.GetImageInfo(ImageType.Primary, 0);
        if (ShouldWriteImage(playlist.HasImage(ImageType.Primary, 0), primary?.Path))
        {
            changed |= await TryCopyAnchorImageAsync(playlist, anchorItemId, ImageType.Primary, libraryManager, cancellationToken)
                || await SaveEmbeddedAsync(playlist, displayName, "primary", ImageType.Primary, cancellationToken);
        }

        var backdrop = playlist.GetImageInfo(ImageType.Backdrop, 0);
        if (ShouldWriteImage(playlist.HasImage(ImageType.Backdrop, 0), backdrop?.Path))
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

    public static bool ShouldWriteImage(bool hasExistingImage, string? existingImagePath)
    {
        if (!hasExistingImage)
            return true;

        if (string.IsNullOrWhiteSpace(existingImagePath) || !File.Exists(existingImagePath))
            return false;

        try
        {
            using var stream = File.OpenRead(existingImagePath);
            return IsLegacyGeneratedArtworkHash(Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsLegacyGeneratedArtworkHash(string hash) => LegacyGeneratedArtworkHashes.Contains(hash);

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
        if (name.Equals("Highly Rated by You", StringComparison.OrdinalIgnoreCase)
            || name.Equals("More Like Your Favorites", StringComparison.OrdinalIgnoreCase)) return "highly-rated";
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
