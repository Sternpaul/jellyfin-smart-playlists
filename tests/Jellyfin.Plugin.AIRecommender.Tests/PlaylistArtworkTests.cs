using System.Reflection;
using System.Security.Cryptography;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PlaylistArtworkTests
{
    private static readonly string[] LegacyArtworkHashes =
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

    public static TheoryData<string, string> ArtworkFamilies => new()
    {
        { "For You", "for-you" },
        { "Because You Watched Arrival", "because-you-watched" },
        { "Hidden Gems", "hidden-gems" },
        { "Recently Added", "recently-added" },
        { "Discover: Hidden World", "discover" },
        { "Wild Card", "wild-card" },
        { "From Your Watchlist", "watchlist" },
        { "More Like Your Favorites", "highly-rated" },
        { "Thriller For You", "subcategory" }
    };

    [Theory]
    [MemberData(nameof(ArtworkFamilies))]
    public void Maps_each_dynamic_family_to_stable_asset_key(string name, string expected)
    {
        Assert.Equal(expected, PlaylistArtworkService.GetAssetKey(name));
    }

    [Fact]
    public void Missing_image_receives_embedded_artwork()
    {
        Assert.True(PlaylistArtworkService.ShouldWriteImage(hasExistingImage: false, existingImagePath: null));
    }

    [Fact]
    public void Unknown_existing_image_is_preserved()
    {
        Assert.False(PlaylistArtworkService.ShouldWriteImage(hasExistingImage: true, existingImagePath: "/not/plugin-generated.png"));
    }

    [Fact]
    public void Legacy_generated_image_is_replaced_once()
    {
        Assert.All(LegacyArtworkHashes, hash => Assert.True(PlaylistArtworkService.IsLegacyGeneratedArtworkHash(hash)));
    }

    [Fact]
    public void Actual_legacy_generated_file_is_replaced()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Jellyfin.Plugin.AIRecommender.Tests.Fixtures.legacy-watchlist-primary.png")!;
        var path = Path.GetTempFileName();
        try
        {
            using (var destination = File.Create(path))
                source.CopyTo(destination);

            Assert.True(PlaylistArtworkService.ShouldWriteImage(hasExistingImage: true, existingImagePath: path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void New_generated_image_is_not_treated_as_legacy()
    {
        Assert.False(PlaylistArtworkService.IsLegacyGeneratedArtworkHash(
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void Every_family_embeds_primary_and_backdrop_png()
    {
        var resources = Assembly.GetAssembly(typeof(PlaylistArtworkService))!.GetManifestResourceNames().ToHashSet();
        foreach (var key in ArtworkFamilies.Select(row => (string)row[1]).Distinct())
        {
            Assert.Contains($"Jellyfin.Plugin.AIRecommender.Assets.Playlists.{key}-primary.png", resources);
            Assert.Contains($"Jellyfin.Plugin.AIRecommender.Assets.Playlists.{key}-backdrop.png", resources);
        }
    }

    [Fact]
    public void Every_new_artwork_resource_is_truecolor_png_with_expected_dimensions_and_not_legacy()
    {
        var assembly = Assembly.GetAssembly(typeof(PlaylistArtworkService))!;
        foreach (var key in ArtworkFamilies.Select(row => (string)row[1]).Distinct())
        {
            AssertPng(assembly, $"Jellyfin.Plugin.AIRecommender.Assets.Playlists.{key}-primary.png", 1000, 1000);
            AssertPng(assembly, $"Jellyfin.Plugin.AIRecommender.Assets.Playlists.{key}-backdrop.png", 1600, 900);
        }
    }

    private static void AssertPng(Assembly assembly, string resourceName, int expectedWidth, int expectedHeight)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var header = new byte[26];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8]);
        Assert.Equal(expectedWidth, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
        Assert.Equal(expectedHeight, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
        Assert.Equal(2, header[25]);

        stream.Position = 0;
        Assert.False(PlaylistArtworkService.IsLegacyGeneratedArtworkHash(
            Convert.ToHexString(SHA256.HashData(stream))));
    }
}
