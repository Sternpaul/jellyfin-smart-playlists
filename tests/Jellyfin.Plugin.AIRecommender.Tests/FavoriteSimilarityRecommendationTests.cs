using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class FavoriteSimilarityRecommendationTests
{
    [Fact]
    public void Rated_titles_are_anchors_and_never_results()
    {
        var favorite = Movie("Favorite");
        var candidate = Movie("Candidate");
        var ranked = FavoriteSimilarityRecommendation.Rank(
            new[] { favorite, candidate },
            new Dictionary<Guid, double> { [favorite.ItemId] = 5.0 },
            new HashSet<Guid>(),
            (source, target) => target.ItemId == candidate.ItemId ? 0.9 : 1.0,
            20);

        Assert.Equal(new[] { candidate.ItemId }, ranked);
        Assert.DoesNotContain(favorite.ItemId, ranked);
    }

    [Fact]
    public void Jellyfin_watched_titles_are_excluded()
    {
        var favorite = Movie("Favorite");
        var watched = Movie("Watched");
        var unwatched = Movie("Unwatched");
        var ranked = FavoriteSimilarityRecommendation.Rank(
            new[] { favorite, watched, unwatched },
            new Dictionary<Guid, double> { [favorite.ItemId] = 5.0 },
            new HashSet<Guid> { watched.ItemId },
            (_, target) => target.ItemId == watched.ItemId ? 1.0 : 0.8,
            20);

        Assert.Equal(new[] { unwatched.ItemId }, ranked);
    }

    [Fact]
    public void Candidates_are_ranked_by_similarity_to_highest_rated_favorites()
    {
        var fiveStar = Movie("Five Star");
        var fourStar = Movie("Four Star");
        var lowRated = Movie("Low Rated");
        var close = Movie("Close");
        var distant = Movie("Distant");
        var ratings = new Dictionary<Guid, double>
        {
            [fiveStar.ItemId] = 5.0,
            [fourStar.ItemId] = 4.0,
            [lowRated.ItemId] = 2.0
        };

        double Similarity(MovieMetadata anchor, MovieMetadata candidate) =>
            candidate.ItemId == close.ItemId
                ? (anchor.ItemId == fiveStar.ItemId ? 0.95 : 0.75)
                : 0.35;

        var ranked = FavoriteSimilarityRecommendation.Rank(
            new[] { fiveStar, fourStar, lowRated, close, distant },
            ratings,
            new HashSet<Guid>(),
            Similarity,
            20);

        Assert.Equal(new[] { close.ItemId, distant.ItemId }, ranked);
    }

    [Fact]
    public void Ratings_below_four_stars_are_not_positive_anchors()
    {
        var disliked = Movie("Disliked");
        var candidate = Movie("Candidate");
        var ranked = FavoriteSimilarityRecommendation.Rank(
            new[] { disliked, candidate },
            new Dictionary<Guid, double> { [disliked.ItemId] = 2.5 },
            new HashSet<Guid>(),
            (_, _) => 1.0,
            20);

        Assert.Empty(ranked);
    }

    private static MovieMetadata Movie(string title) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        IsClassified = true
    };
}
