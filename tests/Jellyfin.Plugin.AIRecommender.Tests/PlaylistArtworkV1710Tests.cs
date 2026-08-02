using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PlaylistArtworkV1710Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "airecommender-art-v1710-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "airecommender.db");

    public PlaylistArtworkV1710Tests()
    {
        Directory.CreateDirectory(_directory);
        using var db = new AiDbContext(DatabasePath);
        db.Database.EnsureCreated();
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Anchor_is_the_representative_when_it_has_usable_local_art()
    {
        var anchor = Guid.NewGuid();
        var ranked = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var selected = RepresentativeArtworkSelector.Select(anchor, ranked, id => id == anchor);

        Assert.Equal(anchor, selected);
    }

    [Fact]
    public void Ranked_order_is_used_when_anchor_is_absent_or_unusable()
    {
        var unavailable = Guid.NewGuid();
        var firstUsable = Guid.NewGuid();
        var laterUsable = Guid.NewGuid();
        var ranked = new[] { unavailable, firstUsable, laterUsable };

        var selected = RepresentativeArtworkSelector.Select(null, ranked, id => id != unavailable);

        Assert.Equal(firstUsable, selected);
    }

    [Fact]
    public void No_representative_is_returned_when_no_ranked_movie_has_local_art()
    {
        Assert.Null(RepresentativeArtworkSelector.Select(null, new[] { Guid.NewGuid() }, _ => false));
    }

    [Fact]
    public void Representative_ranking_keeps_only_final_playlist_members_in_score_order()
    {
        var notSelected = Guid.NewGuid();
        var lowerRankedSelected = Guid.NewGuid();
        var higherRankedSelected = Guid.NewGuid();
        var finalMembers = new[] { lowerRankedSelected, higherRankedSelected };

        var ranked = RepresentativeArtworkSelector.RankFinalMembers(
            new[] { notSelected, higherRankedSelected, lowerRankedSelected },
            finalMembers);

        Assert.Equal(new[] { higherRankedSelected, lowerRankedSelected }, ranked);
    }

    [Fact]
    public void Representative_source_prefers_backdrop_over_primary()
    {
        var itemId = Guid.NewGuid();
        var source = RepresentativeArtworkSelector.SelectSource(
            null,
            new[] { itemId },
            _ => "/movie/backdrop.jpg",
            _ => "/movie/primary.jpg");

        Assert.NotNull(source);
        Assert.Equal(itemId, source!.ItemId);
        Assert.Equal("/movie/backdrop.jpg", source.Path);
        Assert.Equal(ManagedArtworkSourceImageType.Backdrop, source.SourceType);
    }

    [Fact]
    public void Representative_source_uses_primary_then_next_ranked_movie()
    {
        var missing = Guid.NewGuid();
        var primaryOnly = Guid.NewGuid();
        var laterBackdrop = Guid.NewGuid();
        var source = RepresentativeArtworkSelector.SelectSource(
            null,
            new[] { missing, primaryOnly, laterBackdrop },
            id => id == laterBackdrop ? "/later/backdrop.jpg" : null,
            id => id == primaryOnly ? "/first/primary.jpg" : null);

        Assert.NotNull(source);
        Assert.Equal(primaryOnly, source!.ItemId);
        Assert.Equal(ManagedArtworkSourceImageType.Primary, source.SourceType);
    }

    [Fact]
    public void Exact_prior_dynamic_hash_is_replaceable_but_custom_bytes_are_preserved()
    {
        var path = Path.Combine(_directory, "existing.png");
        File.WriteAllBytes(path, "plugin-generated"u8.ToArray());
        var generatedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        Assert.True(PlaylistArtworkService.ShouldWriteImage(true, path, generatedHash));
        Assert.False(PlaylistArtworkService.ShouldWriteImage(true, path, new string('A', 64)));
    }

    [Fact]
    public void Newly_created_playlist_can_replace_readable_unowned_jellyfin_collage()
    {
        var path = Path.Combine(_directory, "jellyfin-collage.png");
        File.WriteAllBytes(path, "jellyfin-generated-four-poster-collage"u8.ToArray());

        Assert.True(PlaylistArtworkService.ShouldWriteImage(
            hasExistingImage: true,
            existingImagePath: path,
            expectedGeneratedHash: null,
            playlistCreatedByCurrentOperation: true));
    }

    [Fact]
    public void Existing_managed_playlist_can_migrate_exact_jellyfin_dynamic_primary_collage()
    {
        var playlistId = Guid.NewGuid();
        var id = playlistId.ToString("N");
        var path = Path.Combine(_directory, "metadata", "library", id[..2], id, "poster.png");
        WritePngHeader(path, 600, 600);

        Assert.True(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId,
            ManagedArtworkImageType.Primary,
            hasExistingImage: true,
            path,
            expectedGeneratedHash: null));
    }

    [Fact]
    public void Legacy_collage_migration_preserves_every_ambiguous_or_custom_variant()
    {
        var playlistId = Guid.NewGuid();
        var id = playlistId.ToString("N");
        var exact = Path.Combine(_directory, "metadata", "library", id[..2], id, "poster.png");
        var custom = Path.Combine(_directory, "data", "playlists", "For You", "folder.png");
        var otherId = Guid.NewGuid().ToString("N");
        var other = Path.Combine(_directory, "metadata", "library", otherId[..2], otherId, "poster.png");
        WritePngHeader(exact, 1000, 1000);
        WritePngHeader(custom, 600, 600);
        WritePngHeader(other, 600, 600);

        Assert.False(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId, ManagedArtworkImageType.Primary, true, exact, null));
        Assert.False(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId, ManagedArtworkImageType.Primary, true, custom, null));
        Assert.False(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId, ManagedArtworkImageType.Primary, true, other, null));
        Assert.False(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId, ManagedArtworkImageType.Backdrop, true, custom, null));
        Assert.False(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId, ManagedArtworkImageType.Primary, false, custom, null));
        Assert.False(PlaylistArtworkService.IsReplaceableLegacyJellyfinPrimaryCollage(
            playlistId, ManagedArtworkImageType.Primary, true, custom, new string('A', 64)));
    }

    [Fact]
    public void New_managed_playlist_is_created_empty_before_artwork_and_members()
    {
        var request = ManagedPlaylistCreationPolicy.CreateEmptyVideoRequest("For You", Guid.NewGuid());

        Assert.Empty(request.ItemIdList);
        Assert.Equal(Jellyfin.Data.Enums.MediaType.Video, request.MediaType);
    }

    [Theory]
    [InlineData(false, null, false, null, true)]
    [InlineData(false, null, true, "AAAA", false)]
    [InlineData(true, "AAAA", true, "aaaa", true)]
    [InlineData(true, "AAAA", true, "BBBB", false)]
    [InlineData(true, "AAAA", false, null, false)]
    public void Snapshot_revalidation_detects_concurrent_image_changes(
        bool hadImage,
        string? observedHash,
        bool hasCurrentImage,
        string? currentHash,
        bool expected)
    {
        Assert.Equal(
            expected,
            PlaylistArtworkService.IsSnapshotStillCurrent(
                hadImage,
                observedHash,
                hasCurrentImage,
                currentHash));
    }

    [Theory]
    [InlineData("AAAA", "aaaa", true)]
    [InlineData("AAAA", "BBBB", false)]
    [InlineData("AAAA", null, false)]
    public void Rollback_only_restores_bytes_still_owned_by_the_attempt(
        string writtenHash,
        string? currentHash,
        bool expected)
    {
        Assert.Equal(expected, PlaylistArtworkService.IsCurrentGeneratedOutput(writtenHash, currentHash));
    }

    [Fact]
    public void Every_v179_static_resource_is_a_safe_one_time_composite_migration_source()
    {
        var assembly = Assembly.GetAssembly(typeof(PlaylistArtworkService))!;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Jellyfin.Plugin.AIRecommender.Assets.Playlists.", StringComparison.Ordinal)
                && name.EndsWith(".png", StringComparison.Ordinal));

        Assert.Equal(18, resources.Count());
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            Assert.True(PlaylistArtworkService.IsCurrentStaticArtworkHash(Convert.ToHexString(SHA256.HashData(stream))));
        }
    }

    [Fact]
    public async Task Existing_database_gets_idempotent_per_image_artwork_provenance()
    {
        using (var db = new AiDbContext(DatabasePath))
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ManagedPlaylistArtwork");

        var store = CreateStore();
        InvokeInitialize(store);
        InvokeInitialize(store);

        var playlistId = Guid.NewGuid();
        await store.SaveManagedPlaylistArtworkAsync(new ManagedPlaylistArtwork
        {
            PlaylistId = playlistId,
            ImageType = ManagedArtworkImageType.Primary,
            GeneratedHash = new string('1', 64),
            SourceItemId = Guid.NewGuid(),
            SourceHash = new string('2', 64),
            RenderedTitle = "For You",
            TemplateVersion = 1,
            UpdatedAt = DateTime.UtcNow
        });
        await store.SaveManagedPlaylistArtworkAsync(new ManagedPlaylistArtwork
        {
            PlaylistId = playlistId,
            ImageType = ManagedArtworkImageType.Backdrop,
            GeneratedHash = new string('3', 64),
            SourceItemId = Guid.NewGuid(),
            SourceHash = new string('4', 64),
            RenderedTitle = "For You",
            TemplateVersion = 1,
            UpdatedAt = DateTime.UtcNow
        });

        var primary = await store.GetManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Primary);
        var backdrop = await store.GetManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Backdrop);
        Assert.NotNull(primary);
        Assert.NotNull(backdrop);
        Assert.NotEqual(primary!.GeneratedHash, backdrop!.GeneratedHash);

        await store.RemoveManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Primary);
        Assert.Null(await store.GetManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Primary));
        Assert.NotNull(await store.GetManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Backdrop));

        await store.SaveManagedPlaylistArtworkAsync(primary!);
        await store.RemoveManagedPlaylistArtworksAsync(
            playlistId,
            new[] { ManagedArtworkImageType.Primary, ManagedArtworkImageType.Backdrop });
        Assert.Null(await store.GetManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Primary));
        Assert.Null(await store.GetManagedPlaylistArtworkAsync(playlistId, ManagedArtworkImageType.Backdrop));
    }

    [Fact]
    public async Task Replacing_a_registered_playlist_id_removes_stale_artwork_provenance()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var oldPlaylistId = Guid.NewGuid();
        var newPlaylistId = Guid.NewGuid();
        await store.UpsertManagedPlaylistAsync(
            userId,
            "recommendation:for-you",
            oldPlaylistId,
            "For You",
            ManagedPlaylistKind.RotatingRecommendation);
        await store.SaveManagedPlaylistArtworkAsync(new ManagedPlaylistArtwork
        {
            PlaylistId = oldPlaylistId,
            ImageType = ManagedArtworkImageType.Backdrop,
            GeneratedHash = new string('A', 64),
            SourceItemId = Guid.NewGuid(),
            SourceImageType = ManagedArtworkSourceImageType.Backdrop,
            SourceHash = new string('B', 64),
            RenderedTitle = "For You",
            TemplateVersion = 1,
            UpdatedAt = DateTime.UtcNow
        });

        await store.UpsertManagedPlaylistAsync(
            userId,
            "recommendation:for-you",
            newPlaylistId,
            "For You",
            ManagedPlaylistKind.RotatingRecommendation);

        Assert.Null(await store.GetManagedPlaylistArtworkAsync(oldPlaylistId, ManagedArtworkImageType.Backdrop));
    }

    private MovieStore CreateStore()
    {
        var store = (MovieStore)RuntimeHelpers.GetUninitializedObject(typeof(MovieStore));
        typeof(MovieStore).GetField("_dbPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, DatabasePath);
        typeof(MovieStore).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, NullLogger<MovieStore>.Instance);
        return store;
    }

    private static void InvokeInitialize(MovieStore store) =>
        typeof(MovieStore).GetMethod("InitializeDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null);

    private static void WritePngHeader(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        File.WriteAllBytes(path, bytes);
    }
}
