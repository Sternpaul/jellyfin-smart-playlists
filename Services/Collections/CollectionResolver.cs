using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services.Collections;

public static class CollectionResolver
{
    public static IReadOnlyList<MovieMetadata> Resolve(
        CollectionDefinition definition,
        IEnumerable<MovieMetadata> movies)
    {
        var tmdbIds = ParseIntIds(definition.TmdbMovieIdsJson);
        var imdbIds = ParseStringIds(definition.ImdbIdsJson);

        return movies
            .Where(movie =>
                (movie.TmdbId.HasValue && tmdbIds.Contains(movie.TmdbId.Value)) ||
                (!string.IsNullOrWhiteSpace(movie.ImdbId) && imdbIds.Contains(movie.ImdbId.Trim())))
            .GroupBy(movie => movie.ItemId)
            .Select(group => group.First())
            .OrderBy(movie => movie.ReleaseYear ?? int.MaxValue)
            .ThenBy(movie => movie.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(movie => movie.ItemId)
            .ToList();
    }

    private static HashSet<int> ParseIntIds(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json ?? "[]")?.Where(id => id > 0).ToHashSet()
                ?? new HashSet<int>();
        }
        catch (JsonException)
        {
            return new HashSet<int>();
        }
    }

    private static HashSet<string> ParseStringIds(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json ?? "[]")?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
