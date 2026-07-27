using System.Reflection;
using System.Security.Cryptography;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
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
    private const int ArtworkTemplateVersion = 1;
    private const long MaximumSourceBytes = 50L * 1024 * 1024;
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
    private static readonly Lazy<HashSet<string>> CurrentStaticArtworkHashes = new(BuildCurrentStaticArtworkHashes);
    private readonly IProviderManager _providerManager;
    private readonly MovieStore _movieStore;
    private readonly ILogger<PlaylistArtworkService> _logger;

    public PlaylistArtworkService(
        IProviderManager providerManager,
        MovieStore movieStore,
        ILogger<PlaylistArtworkService> logger)
    {
        _providerManager = providerManager;
        _movieStore = movieStore;
        _logger = logger;
    }

    public async Task ApplyManagedCompositeAsync(
        Playlist playlist,
        string displayName,
        IReadOnlyList<Guid> rankedItemIds,
        Guid? anchorItemId,
        ILibraryManager libraryManager,
        bool playlistCreatedByCurrentOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(rankedItemIds);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var desired = await BuildDesiredArtworkAsync(
            displayName,
            rankedItemIds,
            anchorItemId,
            libraryManager,
            cancellationToken);
        var primarySnapshot = await CaptureSnapshotAsync(
            playlist,
            ImageType.Primary,
            ManagedArtworkImageType.Primary,
            playlistCreatedByCurrentOperation,
            cancellationToken);
        var backdropSnapshot = await CaptureSnapshotAsync(
            playlist,
            ImageType.Backdrop,
            ManagedArtworkImageType.Backdrop,
            playlistCreatedByCurrentOperation,
            cancellationToken);
        var primaryMutation = new ImageMutationState();
        var backdropMutation = new ImageMutationState();

        try
        {
            var changed = false;
            changed |= await ApplyOneManagedImageAsync(
                playlist,
                displayName,
                primarySnapshot,
                desired.Primary,
                primaryMutation,
                cancellationToken);
            changed |= await ApplyOneManagedImageAsync(
                playlist,
                displayName,
                backdropSnapshot,
                desired.Backdrop,
                backdropMutation,
                cancellationToken);

            RevalidateGeneratedOwnership(playlist, primarySnapshot, primaryMutation);
            RevalidateGeneratedOwnership(playlist, backdropSnapshot, backdropMutation);

            if (changed)
            {
                playlist.OnMetadataChanged();
                await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken);
            }

            var relinquishments = new[]
            {
                (primarySnapshot.ManagedImageType, primaryMutation.RelinquishRequested),
                (backdropSnapshot.ManagedImageType, backdropMutation.RelinquishRequested)
            }
                .Where(entry => entry.RelinquishRequested)
                .Select(entry => entry.ManagedImageType)
                .ToArray();
            await _movieStore.RemoveManagedPlaylistArtworksAsync(
                playlist.Id,
                relinquishments,
                cancellationToken);
        }
        catch (Exception original)
        {
            try
            {
                await RollbackSnapshotsAsync(
                    playlist,
                    (backdropSnapshot, backdropMutation),
                    (primarySnapshot, primaryMutation));
            }
            catch (Exception rollback)
            {
                _logger.LogError(
                    rollback,
                    "Artwork rollback failed for playlist {PlaylistId} after {FailureType}.",
                    playlist.Id,
                    original.GetType().Name);
                throw new AggregateException("Playlist artwork update and rollback both failed.", original, rollback);
            }

            throw;
        }
    }

    private async Task RollbackSnapshotsAsync(
        Playlist playlist,
        params (ImageSnapshot Snapshot, ImageMutationState Mutation)[] changes)
    {
        var failures = new List<Exception>();
        var imageRollbackAttempted = false;
        foreach (var change in changes)
        {
            try
            {
                imageRollbackAttempted |= change.Mutation.ImageWritten;
                await RestoreSnapshotAsync(
                    playlist,
                    change.Snapshot,
                    change.Mutation,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (imageRollbackAttempted)
        {
            try
            {
                playlist.OnMetadataChanged();
                await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more playlist artwork rollback operations failed.", failures);
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
        => ShouldWriteImageCore(hasExistingImage, existingImagePath, expectedGeneratedHash: null, includeCurrentStatic: false);

    public static bool ShouldWriteImage(
        bool hasExistingImage,
        string? existingImagePath,
        string? expectedGeneratedHash)
        => ShouldWriteImageCore(hasExistingImage, existingImagePath, expectedGeneratedHash, includeCurrentStatic: true);

    public static bool ShouldWriteImage(
        bool hasExistingImage,
        string? existingImagePath,
        string? expectedGeneratedHash,
        bool playlistCreatedByCurrentOperation)
    {
        if (playlistCreatedByCurrentOperation && hasExistingImage)
            return TryHashFile(existingImagePath, out _);

        return ShouldWriteImage(hasExistingImage, existingImagePath, expectedGeneratedHash);
    }

    private static bool ShouldWriteImageCore(
        bool hasExistingImage,
        string? existingImagePath,
        string? expectedGeneratedHash,
        bool includeCurrentStatic)
    {
        if (!hasExistingImage)
            return true;

        if (string.IsNullOrWhiteSpace(existingImagePath) || !File.Exists(existingImagePath))
            return false;

        try
        {
            using var stream = File.OpenRead(existingImagePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return IsLegacyGeneratedArtworkHash(hash)
                || (includeCurrentStatic && IsCurrentStaticArtworkHash(hash))
                || (!string.IsNullOrWhiteSpace(expectedGeneratedHash)
                    && hash.Equals(expectedGeneratedHash, StringComparison.OrdinalIgnoreCase));
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

    public static bool IsCurrentStaticArtworkHash(string hash) => CurrentStaticArtworkHashes.Value.Contains(hash);

    private static HashSet<string> BuildCurrentStaticArtworkHashes()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix + ".", StringComparison.Ordinal)
                         && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded playlist artwork resource '{resourceName}'.");
            hashes.Add(Convert.ToHexString(SHA256.HashData(stream)));
        }

        return hashes;
    }

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

    private async Task<DesiredArtworkPair> BuildDesiredArtworkAsync(
        string displayName,
        IReadOnlyList<Guid> rankedItemIds,
        Guid? anchorItemId,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        var remaining = rankedItemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var remainingAnchor = anchorItemId;
        var rejectedPaths = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = RepresentativeArtworkSelector.SelectSource(
                remainingAnchor,
                remaining,
                id => ExcludeRejected(TryGetLocalImagePath(libraryManager, id, ImageType.Backdrop), rejectedPaths),
                id => ExcludeRejected(TryGetLocalImagePath(libraryManager, id, ImageType.Primary), rejectedPaths));
            if (source == null)
                break;

            try
            {
                var info = new FileInfo(source.Path);
                if (info.Length <= 0 || info.Length > MaximumSourceBytes)
                    throw new InvalidDataException($"Source artwork is {info.Length} bytes.");
                var sourceBytes = await File.ReadAllBytesAsync(source.Path, cancellationToken);
                var sourceHash = Hash(sourceBytes);
                return new DesiredArtworkPair(
                    new DesiredArtwork(
                        PlaylistArtworkComposer.Compose(sourceBytes, displayName, 1000, 1000),
                        source.ItemId,
                        source.SourceType,
                        sourceHash),
                    new DesiredArtwork(
                        PlaylistArtworkComposer.Compose(sourceBytes, displayName, 1600, 900),
                        source.ItemId,
                        source.SourceType,
                        sourceHash));
            }
            catch (PlaylistArtworkFontUnavailableException ex)
            {
                _logger.LogWarning(
                    ex,
                    "No verified bold sans-serif typeface is available; using embedded static artwork for playlist '{DisplayName}'.",
                    displayName);
                return new DesiredArtworkPair(
                    LoadEmbeddedDesired(displayName, "primary"),
                    LoadEmbeddedDesired(displayName, "backdrop"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not compose playlist artwork from item {SourceItemId}; trying its next local image or the next ranked movie.",
                    source.ItemId);
                rejectedPaths.Add(source.Path);
            }
        }

        return new DesiredArtworkPair(
            LoadEmbeddedDesired(displayName, "primary"),
            LoadEmbeddedDesired(displayName, "backdrop"));
    }

    private async Task<bool> ApplyOneManagedImageAsync(
        Playlist playlist,
        string displayName,
        ImageSnapshot snapshot,
        DesiredArtwork desired,
        ImageMutationState mutation,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Authorized)
        {
            if (snapshot.RelinquishOwnership)
                mutation.RelinquishRequested = true;

            return false;
        }

        if (!SnapshotStillCurrent(playlist, snapshot))
        {
            var current = playlist.GetImageInfo(snapshot.ImageType, 0);
            if (snapshot.PriorProvenance != null && TryHashFile(current?.Path, out _))
                mutation.RelinquishRequested = true;

            return false;
        }

        var desiredHash = Hash(desired.Bytes);
        var existing = playlist.GetImageInfo(snapshot.ImageType, 0);
        if (playlist.HasImage(snapshot.ImageType, 0) && TryHashFile(existing?.Path, out var existingHash)
            && existingHash.Equals(desiredHash, StringComparison.OrdinalIgnoreCase))
        {
            if (ProvenanceMatches(snapshot.PriorProvenance, displayName, desired, desiredHash))
                return false;

            await SaveProvenanceAsync(
                playlist.Id,
                displayName,
                snapshot.ManagedImageType,
                desired,
                desiredHash,
                cancellationToken);
            mutation.ProvenanceUpdated = true;
            mutation.ExpectedOwnedHash = desiredHash;
            return false;
        }

        await using var stream = new MemoryStream(desired.Bytes, writable: false);
        try
        {
            await _providerManager.SaveImage(playlist, stream, "image/png", snapshot.ImageType, 0, cancellationToken);
        }
        finally
        {
            var written = playlist.GetImageInfo(snapshot.ImageType, 0);
            if (TryHashFile(written?.Path, out var writtenHash)
                && writtenHash.Equals(desiredHash, StringComparison.OrdinalIgnoreCase))
            {
                mutation.ImageWritten = true;
                mutation.WrittenHash = desiredHash;
            }
        }

        if (!mutation.ImageWritten)
        {
            var current = playlist.GetImageInfo(snapshot.ImageType, 0);
            if (TryHashFile(current?.Path, out _))
                mutation.RelinquishRequested = true;
            return false;
        }

        await SaveProvenanceAsync(
            playlist.Id,
            displayName,
            snapshot.ManagedImageType,
            desired,
            desiredHash,
            cancellationToken);
        mutation.ProvenanceUpdated = true;
        mutation.ExpectedOwnedHash = desiredHash;
        return true;
    }

    private static void RevalidateGeneratedOwnership(
        Playlist playlist,
        ImageSnapshot snapshot,
        ImageMutationState mutation)
    {
        if (!mutation.ProvenanceUpdated || mutation.ExpectedOwnedHash == null)
            return;

        var current = playlist.GetImageInfo(snapshot.ImageType, 0);
        if (!TryHashFile(current?.Path, out var currentHash)
            || !IsCurrentGeneratedOutput(mutation.ExpectedOwnedHash, currentHash))
        {
            mutation.RelinquishRequested = true;
        }
    }

    private static bool SnapshotStillCurrent(Playlist playlist, ImageSnapshot snapshot)
    {
        var hasCurrent = playlist.HasImage(snapshot.ImageType, 0);
        var current = playlist.GetImageInfo(snapshot.ImageType, 0);
        var hasCurrentHash = TryHashFile(current?.Path, out var currentHash);
        return IsSnapshotStillCurrent(
            snapshot.HadImage,
            snapshot.ObservedHash,
            hasCurrent,
            hasCurrentHash ? currentHash : null);
    }

    public static bool IsSnapshotStillCurrent(
        bool hadImage,
        string? observedHash,
        bool hasCurrentImage,
        string? currentHash)
    {
        if (!hadImage)
            return !hasCurrentImage;
        return hasCurrentImage
            && !string.IsNullOrWhiteSpace(observedHash)
            && !string.IsNullOrWhiteSpace(currentHash)
            && currentHash.Equals(observedHash, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentGeneratedOutput(string? writtenHash, string? currentHash)
        => !string.IsNullOrWhiteSpace(writtenHash)
            && !string.IsNullOrWhiteSpace(currentHash)
            && currentHash.Equals(writtenHash, StringComparison.OrdinalIgnoreCase);

    private static bool ProvenanceMatches(
        ManagedPlaylistArtwork? prior,
        string displayName,
        DesiredArtwork desired,
        string generatedHash)
        => prior != null
            && prior.GeneratedHash.Equals(generatedHash, StringComparison.OrdinalIgnoreCase)
            && prior.SourceItemId == desired.SourceItemId
            && prior.SourceImageType == desired.SourceType
            && prior.SourceHash.Equals(desired.SourceHash, StringComparison.OrdinalIgnoreCase)
            && prior.RenderedTitle.Equals(displayName, StringComparison.Ordinal)
            && prior.TemplateVersion == ArtworkTemplateVersion;

    private async Task<ImageSnapshot> CaptureSnapshotAsync(
        Playlist playlist,
        ImageType imageType,
        ManagedArtworkImageType managedImageType,
        bool playlistCreatedByCurrentOperation,
        CancellationToken cancellationToken)
    {
        var prior = await _movieStore.GetManagedPlaylistArtworkAsync(playlist.Id, managedImageType, cancellationToken);
        var existing = playlist.GetImageInfo(imageType, 0);
        var hasExisting = playlist.HasImage(imageType, 0);
        var hasReadableHash = TryHashFile(existing?.Path, out var observedHash);
        var authorized = ShouldWriteImage(
            hasExisting,
            existing?.Path,
            prior?.GeneratedHash,
            playlistCreatedByCurrentOperation);
        var relinquishOwnership = hasExisting
            && prior != null
            && hasReadableHash
            && !authorized;
        if (!authorized || !hasExisting)
            return new ImageSnapshot(
                imageType,
                managedImageType,
                prior,
                hasExisting,
                null,
                hasReadableHash ? observedHash : null,
                authorized,
                relinquishOwnership);

        try
        {
            var originalBytes = await File.ReadAllBytesAsync(existing!.Path, cancellationToken);
            return new ImageSnapshot(
                imageType,
                managedImageType,
                prior,
                true,
                originalBytes,
                observedHash,
                true,
                false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not snapshot authorized {ImageType} artwork for playlist {PlaylistId}; preserving it unchanged.",
                imageType,
                playlist.Id);
            return new ImageSnapshot(
                imageType,
                managedImageType,
                prior,
                true,
                null,
                observedHash,
                false,
                false);
        }
    }

    private async Task RestoreSnapshotAsync(
        Playlist playlist,
        ImageSnapshot snapshot,
        ImageMutationState mutation,
        CancellationToken cancellationToken)
    {
        if (!mutation.ImageWritten && !mutation.ProvenanceUpdated)
            return;

        var failures = new List<Exception>();
        var preserveCurrentAsCustom = false;
        if (mutation.ImageWritten)
        {
            var current = playlist.GetImageInfo(snapshot.ImageType, 0);
            var hasCurrentHash = TryHashFile(current?.Path, out var currentHash);
            preserveCurrentAsCustom = !IsCurrentGeneratedOutput(
                mutation.WrittenHash,
                hasCurrentHash ? currentHash : null);

            if (!preserveCurrentAsCustom)
            {
                try
                {
                    if (snapshot.HadImage)
                    {
                        if (snapshot.OriginalBytes == null)
                            throw new InvalidOperationException($"Missing rollback bytes for {snapshot.ImageType}.");
                        await using var stream = new MemoryStream(snapshot.OriginalBytes, writable: false);
                        await _providerManager.SaveImage(
                            playlist,
                            stream,
                            "image/png",
                            snapshot.ImageType,
                            0,
                            cancellationToken);
                    }
                    else if (playlist.HasImage(snapshot.ImageType, 0))
                    {
                        await playlist.DeleteImageAsync(snapshot.ImageType, 0);
                    }
                }
                catch (Exception ex)
                {
                    preserveCurrentAsCustom = true;
                    failures.Add(ex);
                }

                if (!preserveCurrentAsCustom)
                {
                    var restored = playlist.GetImageInfo(snapshot.ImageType, 0);
                    var hasRestored = playlist.HasImage(snapshot.ImageType, 0);
                    var hasRestoredHash = TryHashFile(restored?.Path, out var restoredHash);
                    preserveCurrentAsCustom = !IsSnapshotStillCurrent(
                        snapshot.HadImage,
                        snapshot.ObservedHash,
                        hasRestored,
                        hasRestoredHash ? restoredHash : null);
                }
            }
        }
        else if (mutation.ProvenanceUpdated)
        {
            var current = playlist.GetImageInfo(snapshot.ImageType, 0);
            var hasCurrentHash = TryHashFile(current?.Path, out var currentHash);
            preserveCurrentAsCustom = !IsCurrentGeneratedOutput(
                mutation.ExpectedOwnedHash,
                hasCurrentHash ? currentHash : null);
        }

        try
        {
            if (preserveCurrentAsCustom || snapshot.PriorProvenance == null)
            {
                await _movieStore.RemoveManagedPlaylistArtworkAsync(
                    playlist.Id,
                    snapshot.ManagedImageType,
                    cancellationToken);
            }
            else
            {
                await _movieStore.SaveManagedPlaylistArtworkAsync(snapshot.PriorProvenance, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
            throw new AggregateException($"Rollback failed for {snapshot.ImageType} artwork.", failures);
    }

    private Task SaveProvenanceAsync(
        Guid playlistId,
        string displayName,
        ManagedArtworkImageType imageType,
        DesiredArtwork desired,
        string generatedHash,
        CancellationToken cancellationToken)
        => _movieStore.SaveManagedPlaylistArtworkAsync(new ManagedPlaylistArtwork
        {
            PlaylistId = playlistId,
            ImageType = imageType,
            GeneratedHash = generatedHash,
            SourceItemId = desired.SourceItemId,
            SourceImageType = desired.SourceType,
            SourceHash = desired.SourceHash,
            RenderedTitle = displayName,
            TemplateVersion = ArtworkTemplateVersion,
            UpdatedAt = DateTime.UtcNow
        }, cancellationToken);

    private static string? ExcludeRejected(string? path, HashSet<string> rejectedPaths)
        => !string.IsNullOrWhiteSpace(path) && !rejectedPaths.Contains(path) ? path : null;

    private static string? TryGetLocalImagePath(ILibraryManager libraryManager, Guid itemId, ImageType imageType)
    {
        if (libraryManager.GetItemById(itemId) is not BaseItem item)
            return null;

        var image = item.GetImageInfo(imageType, 0);
        return image != null && !string.IsNullOrWhiteSpace(image.Path) && File.Exists(image.Path)
            ? image.Path
            : null;
    }

    private static DesiredArtwork LoadEmbeddedDesired(string displayName, string shape)
    {
        var resourceName = $"{ResourcePrefix}.{GetAssetKey(displayName)}-{shape}.png";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded playlist artwork resource '{resourceName}'.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        return new DesiredArtwork(
            bytes,
            null,
            ManagedArtworkSourceImageType.EmbeddedFallback,
            Hash(bytes));
    }

    private static bool TryHashFile(string? path, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream));
            return true;
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

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string MimeTypeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };

    private sealed record DesiredArtwork(
        byte[] Bytes,
        Guid? SourceItemId,
        ManagedArtworkSourceImageType SourceType,
        string SourceHash);
    private sealed record DesiredArtworkPair(DesiredArtwork Primary, DesiredArtwork Backdrop);
    private sealed record ImageSnapshot(
        ImageType ImageType,
        ManagedArtworkImageType ManagedImageType,
        ManagedPlaylistArtwork? PriorProvenance,
        bool HadImage,
        byte[]? OriginalBytes,
        string? ObservedHash,
        bool Authorized,
        bool RelinquishOwnership);

    private sealed class ImageMutationState
    {
        public bool ImageWritten { get; set; }
        public string? WrittenHash { get; set; }
        public bool ProvenanceUpdated { get; set; }
        public string? ExpectedOwnedHash { get; set; }
        public bool RelinquishRequested { get; set; }
    }
}
