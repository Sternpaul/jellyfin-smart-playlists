using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class RatingsWatchedPolicyTests
{
    [Fact]
    public void Every_current_user_rating_key_is_excluded_regardless_of_score()
    {
        var fiveStar = Movie("Five Star");
        var lowRated = Movie("Low Rated");
        var watchedOnly = Movie("Watched Only");
        var anotherUsersRatedMovie = Movie("Another User");
        var unrated = Movie("Unrated");
        var currentUserRatings = new Dictionary<Guid, double>
        {
            [fiveStar.ItemId] = 5.0,
            [lowRated.ItemId] = 0.5,
            [watchedOnly.ItemId] = 0.0
        };

        var eligible = RatingsWatchedPolicy.ExcludeRatedMovies(
            new[] { fiveStar, lowRated, watchedOnly, anotherUsersRatedMovie, unrated },
            currentUserRatings);

        Assert.Equal(
            new[] { anotherUsersRatedMovie.ItemId, unrated.ItemId },
            eligible.Select(movie => movie.ItemId));
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(2.5, 2.5)]
    [InlineData(5.0, 5.0)]
    [InlineData(6.0, 0.0)]
    public void Every_matched_json_entry_gets_a_watched_marker(double sourceScore, double storedScore)
    {
        Assert.Equal(storedScore, RatingsWatchedPolicy.NormalizeImportedScore(sourceScore));
    }

    private static MovieMetadata Movie(string title) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        IsClassified = true
    };
}
