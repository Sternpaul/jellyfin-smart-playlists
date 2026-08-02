using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PlaylistDescriptionTests
{
    private static readonly IReadOnlyList<MovieMetadata> CurrentSelection =
    [
        Movie("Political Thriller", "Tense", "Power and Corruption"),
        Movie("Political Thriller", "Tense", "Power and Corruption"),
        Movie("War Epic", "Somber", "Survival")
    ];

    public static TheoryData<string, string> DynamicDescriptions => new()
    {
        { "For You", "Selected to match your taste, featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "Hidden Gems", "Acclaimed, lesser-known discoveries featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "Recently Added", "New arrivals in your library featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "Discover: Hidden World", "A step beyond your usual favorites, exploring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "Wild Card", "An adventurous change of pace featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "From Your Watchlist", "Unwatched picks from your watchlist featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "More Like Your Favorites", "Chosen for their similarity to films you rated highly, featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "Thriller For You", "Thriller picks matched to your taste, featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." },
        { "Because You Watched Arrival", "Chosen for their similarity to Arrival, featuring a mix of Political Thriller and War Epic, themes including Power and Corruption and Survival, and a Tense mood." }
    };

    [Theory]
    [MemberData(nameof(DynamicDescriptions))]
    public void Explains_why_the_current_selection_belongs(string name, string expected)
    {
        Assert.Equal(expected, PlaylistDescriptionBuilder.Build(name, CurrentSelection));
    }

    [Fact]
    public void Never_exposes_refresh_or_item_count_implementation_details()
    {
        var actual = PlaylistDescriptionBuilder.Build("Because You Watched Arrival", CurrentSelection);

        Assert.DoesNotContain("manual", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("flag", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotate", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contains", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("films.", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UTC", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uses_an_understandable_reason_when_classification_is_missing()
    {
        Assert.Equal(
            "Selected because they match the kinds of films you tend to enjoy.",
            PlaylistDescriptionBuilder.Build("For You", []));
    }

    [Fact]
    public void Uses_the_correct_article_for_a_vowel_sound_mood()
    {
        var selection = new[] { Movie("Drama", "Emotional", "Family") };

        Assert.Contains("an Emotional mood", PlaylistDescriptionBuilder.Build("For You", selection));
    }

    [Theory]
    [InlineData(null, "My Collection is a collection selected by your Jellyfin administrator, ordered by release year.")]
    [InlineData("Award-winning documentaries.", "Award-winning documentaries.")]
    public void Persistent_collection_uses_the_administrator_reason_without_operational_metadata(
        string? administratorDescription,
        string expected)
    {
        Assert.Equal(
            expected,
            PlaylistDescriptionBuilder.BuildPersistentCollection("My Collection", administratorDescription));
    }

    private static MovieMetadata Movie(string subcategory, string mood, string theme)
        => new()
        {
            ItemId = Guid.NewGuid(),
            Subcategories = JsonSerializer.Serialize(new[] { subcategory }),
            Moods = JsonSerializer.Serialize(new[] { mood }),
            Themes = JsonSerializer.Serialize(new[] { theme }),
            IsClassified = true
        };
}
