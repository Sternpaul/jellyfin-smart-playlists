using System.Reflection;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PlaylistArtworkTests
{
    public static TheoryData<string, string> ArtworkFamilies => new()
    {
        { "For You", "for-you" },
        { "Because You Watched Arrival", "because-you-watched" },
        { "Hidden Gems", "hidden-gems" },
        { "Recently Added", "recently-added" },
        { "Discover: Hidden World", "discover" },
        { "Wild Card", "wild-card" },
        { "From Your Watchlist", "watchlist" },
        { "Highly Rated by You", "highly-rated" },
        { "Thriller For You", "subcategory" }
    };

    [Theory]
    [MemberData(nameof(ArtworkFamilies))]
    public void Maps_each_dynamic_family_to_stable_asset_key(string name, string expected)
    {
        Assert.Equal(expected, PlaylistArtworkService.GetAssetKey(name));
    }

    [Fact]
    public void Existing_image_is_never_overwritten()
    {
        Assert.False(PlaylistArtworkService.ShouldWriteImage(hasExistingImage: true));
        Assert.True(PlaylistArtworkService.ShouldWriteImage(hasExistingImage: false));
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
}
