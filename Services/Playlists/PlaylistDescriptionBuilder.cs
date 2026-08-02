using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data.Models;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public static class PlaylistDescriptionBuilder
{
    public static string Build(string displayName, IReadOnlyCollection<MovieMetadata> currentMovies)
    {
        var name = displayName.Trim();
        var selection = DescribeSelection(currentMovies);
        if (selection == null)
            return GetFallbackExplanation(name);

        if (name.StartsWith("Because You Watched ", StringComparison.OrdinalIgnoreCase))
        {
            var anchor = name["Because You Watched ".Length..].Trim();
            return $"Chosen for their similarity to {anchor}, featuring {selection}.";
        }

        if (name.Equals("For You", StringComparison.OrdinalIgnoreCase))
            return $"Selected to match your taste, featuring {selection}.";
        if (name.Equals("Hidden Gems", StringComparison.OrdinalIgnoreCase))
            return $"Acclaimed, lesser-known discoveries featuring {selection}.";
        if (name.Equals("Recently Added", StringComparison.OrdinalIgnoreCase))
            return $"New arrivals in your library featuring {selection}.";
        if (name.Equals("Discover: Hidden World", StringComparison.OrdinalIgnoreCase))
            return $"A step beyond your usual favorites, exploring {selection}.";
        if (name.Equals("Wild Card", StringComparison.OrdinalIgnoreCase))
            return $"An adventurous change of pace featuring {selection}.";
        if (name.Equals("From Your Watchlist", StringComparison.OrdinalIgnoreCase))
            return $"Unwatched picks from your watchlist featuring {selection}.";
        if (name.Equals("More Like Your Favorites", StringComparison.OrdinalIgnoreCase))
            return $"Chosen for their similarity to films you rated highly, featuring {selection}.";
        if (name.EndsWith(" For You", StringComparison.OrdinalIgnoreCase))
        {
            var category = name[..^" For You".Length].Trim();
            return $"{category} picks matched to your taste, featuring {selection}.";
        }

        return $"Selected to fit this recommendation, featuring {selection}.";
    }

    public static string BuildPersistentCollection(string displayName, string? administratorDescription)
    {
        if (!string.IsNullOrWhiteSpace(administratorDescription))
            return EnsureTerminalPunctuation(administratorDescription.Trim());

        return $"{displayName.Trim()} is a collection selected by your Jellyfin administrator, ordered by release year.";
    }

    private static string? DescribeSelection(IReadOnlyCollection<MovieMetadata> movies)
    {
        if (movies.Count == 0)
            return null;

        var details = new List<string>();
        var subcategories = TopJsonValues(movies, movie => movie.Subcategories, 2);
        var themes = TopJsonValues(movies, movie => movie.Themes, 2);
        var moods = TopJsonValues(movies, movie => movie.Moods, 1);

        if (subcategories.Count > 0)
            details.Add($"a mix of {JoinNaturally(subcategories)}");
        if (themes.Count > 0)
            details.Add($"themes including {JoinNaturally(themes)}");
        if (moods.Count > 0)
            details.Add($"{IndefiniteArticle(moods[0])} {moods[0]} mood");

        return details.Count == 0 ? null : JoinNaturally(details);
    }

    private static List<string> TopJsonValues(
        IEnumerable<MovieMetadata> movies,
        Func<MovieMetadata, string?> selector,
        int count)
    {
        return movies
            .SelectMany(movie => ParseValues(selector(movie))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Value = group.OrderBy(value => value, StringComparer.Ordinal).First(),
                Count = group.Count()
            })
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(value => value.Value)
            .ToList();
    }

    private static IEnumerable<string> ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Select(NormalizeValue)
                .Where(value => value != null)
                .Cast<string>()
                .Where(value => !value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                                && !value.Equals("none", StringComparison.OrdinalIgnoreCase)
                                && !value.Equals("n/a", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 60 ? normalized : null;
    }

    private static string JoinNaturally(IReadOnlyList<string> values)
        => values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
        };

    private static string IndefiniteArticle(string value)
        => value.Length > 0 && "aeiouAEIOU".Contains(value[0]) ? "an" : "a";

    private static string GetFallbackExplanation(string displayName)
    {
        if (displayName.StartsWith("Because You Watched ", StringComparison.OrdinalIgnoreCase))
        {
            var anchor = displayName["Because You Watched ".Length..].Trim();
            return $"Selected because they share qualities with {anchor}.";
        }

        if (displayName.Equals("For You", StringComparison.OrdinalIgnoreCase))
            return "Selected because they match the kinds of films you tend to enjoy.";
        if (displayName.Equals("Hidden Gems", StringComparison.OrdinalIgnoreCase))
            return "Acclaimed, lesser-known films chosen to offer something beyond the obvious picks.";
        if (displayName.Equals("Recently Added", StringComparison.OrdinalIgnoreCase))
            return "Unwatched films that recently joined your Jellyfin library.";
        if (displayName.Equals("Discover: Hidden World", StringComparison.OrdinalIgnoreCase))
            return "Films chosen to explore beyond your usual favorites.";
        if (displayName.Equals("Wild Card", StringComparison.OrdinalIgnoreCase))
            return "An adventurous change of pace outside your usual recommendations.";
        if (displayName.Equals("From Your Watchlist", StringComparison.OrdinalIgnoreCase))
            return "Available, unwatched films from your personal watchlist.";
        if (displayName.Equals("More Like Your Favorites", StringComparison.OrdinalIgnoreCase))
            return "Unwatched films chosen for their similarity to movies you rated highly.";
        if (displayName.EndsWith(" For You", StringComparison.OrdinalIgnoreCase))
        {
            var category = displayName[..^" For You".Length].Trim();
            return $"{category} films chosen to match your taste.";
        }

        return "Films selected because they fit this recommendation's theme.";
    }

    private static string EnsureTerminalPunctuation(string value)
        => value.EndsWith(".", StringComparison.Ordinal)
           || value.EndsWith("!", StringComparison.Ordinal)
           || value.EndsWith("?", StringComparison.Ordinal)
            ? value
            : value + ".";
}
