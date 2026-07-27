using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PlaylistDescriptionTests
{
    private static readonly DateTime RefreshedAt = new(2026, 7, 26, 22, 30, 0, DateTimeKind.Utc);

    public static TheoryData<string, string> DynamicDescriptions => new()
    {
        { "For You", "Unwatched films selected from your verified viewing, long-term taste, ratings, and discovery settings. Rotates on refresh." },
        { "Hidden Gems", "Less obvious unwatched films matched to your verified viewing and taste profile. Rotates on refresh." },
        { "Recently Added", "Recently added unwatched films from your Jellyfin library. Rotates on refresh." },
        { "Discover: Hidden World", "A varied discovery mix outside your usual strongest preferences, balanced by your configured discovery settings. Rotates on refresh." },
        { "Wild Card", "An intentionally adventurous unwatched pick from outside your usual recommendations. Rotates on refresh." },
        { "From Your Watchlist", "Unwatched films matched from your configured watchlist source and available in Jellyfin. Rotates on refresh." },
        { "More Like Your Favorites", "Unwatched, unrated films in Jellyfin ranked by similarity to your 4-star-and-higher Letterboxd favorites. Your rated films are taste anchors and are never included. Rotates on refresh." },
        { "Thriller For You", "Unwatched Thriller films matched to your verified viewing and taste profile. Rotates on refresh." },
        { "Because You Watched Arrival", "Movies most similar to Arrival and your other recent verified Jellyfin watches. Manual Played flags are not used as recent watches. Rotates on refresh." }
    };

    [Theory]
    [MemberData(nameof(DynamicDescriptions))]
    public void Builds_safe_native_overview_for_each_dynamic_family(string name, string expectedExplanation)
    {
        var actual = PlaylistDescriptionBuilder.Build(name, 20, RefreshedAt);
        Assert.Equal($"{expectedExplanation} Contains 20 films. Last refreshed 2026-07-26 22:30 UTC.", actual);
    }

    [Fact]
    public void Uses_singular_item_count()
    {
        var actual = PlaylistDescriptionBuilder.Build("For You", 1, RefreshedAt);
        Assert.Contains("Contains 1 film.", actual, StringComparison.Ordinal);
    }
}
