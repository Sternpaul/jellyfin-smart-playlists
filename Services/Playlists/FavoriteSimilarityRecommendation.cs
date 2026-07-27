using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public static class FavoriteSimilarityRecommendation
{
    private const double MinimumFavoriteRating = 4.0;
    private const int MaximumAnchors = 10;
    private const int StrongestMatchesPerCandidate = 3;

    public static IReadOnlyList<Guid> Rank(
        IEnumerable<MovieMetadata> movies,
        IReadOnlyDictionary<Guid, double> ratings,
        IReadOnlySet<Guid> excludedItemIds,
        Func<MovieMetadata, MovieMetadata, double> similarity,
        int maxCount)
    {
        if (maxCount <= 0)
            return Array.Empty<Guid>();

        var library = movies
            .Where(movie => movie.IsClassified)
            .GroupBy(movie => movie.ItemId)
            .Select(group => group.First())
            .ToList();
        var byId = library.ToDictionary(movie => movie.ItemId);
        var anchors = ratings
            .Where(entry => entry.Value >= MinimumFavoriteRating && byId.ContainsKey(entry.Key))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key)
            .Take(MaximumAnchors)
            .Select(entry => (Movie: byId[entry.Key], Weight: (entry.Value - 3.0) / 2.0))
            .ToList();

        if (anchors.Count == 0)
            return Array.Empty<Guid>();

        return library
            .Where(candidate => !ratings.ContainsKey(candidate.ItemId))
            .Where(candidate => !excludedItemIds.Contains(candidate.ItemId))
            .Select(candidate => new
            {
                candidate.ItemId,
                Score = anchors
                    .Select(anchor => similarity(anchor.Movie, candidate) * anchor.Weight)
                    .OrderByDescending(score => score)
                    .Take(StrongestMatchesPerCandidate)
                    .DefaultIfEmpty(0.0)
                    .Average()
            })
            .Where(result => result.Score > 0.0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.ItemId)
            .Take(maxCount)
            .Select(result => result.ItemId)
            .ToList();
    }
}
