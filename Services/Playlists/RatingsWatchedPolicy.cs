using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public static class RatingsWatchedPolicy
{
    public static List<MovieMetadata> ExcludeRatedMovies(
        IEnumerable<MovieMetadata> candidates,
        IReadOnlyDictionary<Guid, double> currentUserRatings)
        => candidates
            .Where(movie => !currentUserRatings.ContainsKey(movie.ItemId))
            .ToList();

    public static double NormalizeImportedScore(double score)
        => double.IsFinite(score) && score > 0.0 && score <= 5.0
            ? score
            : 0.0;
}
