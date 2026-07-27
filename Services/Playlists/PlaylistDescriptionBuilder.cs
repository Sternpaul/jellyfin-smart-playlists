using System;

namespace Jellyfin.Plugin.AIRecommender.Services.Playlists;

public static class PlaylistDescriptionBuilder
{
    public static string Build(string displayName, int itemCount, DateTime refreshedAt)
    {
        var explanation = GetExplanation(displayName.Trim());
        var noun = itemCount == 1 ? "film" : "films";
        return $"{explanation} Contains {itemCount} {noun}. Last refreshed {refreshedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.";
    }

    public static string BuildPersistentCollection(
        string displayName,
        string? administratorDescription,
        int itemCount,
        DateTime refreshedAt)
    {
        var noun = itemCount == 1 ? "film" : "films";
        var detail = string.IsNullOrWhiteSpace(administratorDescription)
            ? string.Empty
            : $" {administratorDescription.Trim()}";
        return $"{displayName} is a persistent collection assigned by your Jellyfin administrator.{detail} " +
               $"Contains {itemCount} available {noun}, ordered by release year. " +
               $"It does not rotate with recommendations. Last refreshed {refreshedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.";
    }

    private static string GetExplanation(string displayName)
    {
        if (displayName.StartsWith("Because You Watched ", StringComparison.OrdinalIgnoreCase))
        {
            var anchor = displayName["Because You Watched ".Length..];
            return $"Movies most similar to {anchor} and your other recent verified Jellyfin watches. Manual Played flags are not used as recent watches. Rotates on refresh.";
        }

        if (displayName.Equals("For You", StringComparison.OrdinalIgnoreCase))
            return "Unwatched films selected from your verified viewing, long-term taste, ratings, and discovery settings. Rotates on refresh.";
        if (displayName.Equals("Hidden Gems", StringComparison.OrdinalIgnoreCase))
            return "Less obvious unwatched films matched to your verified viewing and taste profile. Rotates on refresh.";
        if (displayName.Equals("Recently Added", StringComparison.OrdinalIgnoreCase))
            return "Recently added unwatched films from your Jellyfin library. Rotates on refresh.";
        if (displayName.Equals("Discover: Hidden World", StringComparison.OrdinalIgnoreCase))
            return "A varied discovery mix outside your usual strongest preferences, balanced by your configured discovery settings. Rotates on refresh.";
        if (displayName.Equals("Wild Card", StringComparison.OrdinalIgnoreCase))
            return "An intentionally adventurous unwatched pick from outside your usual recommendations. Rotates on refresh.";
        if (displayName.Equals("From Your Watchlist", StringComparison.OrdinalIgnoreCase))
            return "Unwatched films matched from your configured watchlist source and available in Jellyfin. Rotates on refresh.";
        if (displayName.Equals("More Like Your Favorites", StringComparison.OrdinalIgnoreCase))
            return "Unwatched, unrated films in Jellyfin ranked by similarity to your 4-star-and-higher Letterboxd favorites. Your rated films are taste anchors and are never included. Rotates on refresh.";
        if (displayName.EndsWith(" For You", StringComparison.OrdinalIgnoreCase))
        {
            var category = displayName[..^" For You".Length];
            return $"Unwatched {category} films matched to your verified viewing and taste profile. Rotates on refresh.";
        }

        return "Unwatched films selected by AI Recommender from your Jellyfin library and configured taste signals. Rotates on refresh.";
    }
}
