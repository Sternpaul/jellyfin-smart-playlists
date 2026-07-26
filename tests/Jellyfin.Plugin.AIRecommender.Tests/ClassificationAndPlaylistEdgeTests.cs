using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class ClassificationAndPlaylistEdgeTests
{
    [Fact]
    public void Comma_separated_text_fallback_classifies_without_json_exception()
    {
        var classifier = new MovieClassifier(null!, null!, NullLogger<MovieClassifier>.Instance);
        var movie = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Oldboy" };
        const string response = "**Oldboy*: Action, Drama. Moods: Dark, Tense. Themes: Revenge, Identity. Style: Nonlinear. Accessibility: Challenging. Intensity: High. Score: 9";
        var method = typeof(MovieClassifier).GetMethod("ProcessClassificationResult", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var exception = Record.Exception(() => method.Invoke(classifier, new object?[] { response, new List<MovieMetadata> { movie } }));

        Assert.Null(exception);
        Assert.True(movie.IsClassified);
        Assert.Equal(new[] { "Action", "Drama" }, JsonSerializer.Deserialize<List<string>>(movie.Subcategories!));
    }

    [Fact]
    public void Empty_because_you_watched_candidates_return_no_anchor()
    {
        var method = typeof(PlaylistEngine).GetMethod("SelectBecauseYouWatchedAnchor", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var anchor = method!.Invoke(null, new object[]
        {
            new List<Guid>(),
            new Dictionary<Guid, MovieMetadata>(),
            new List<MovieMetadata>()
        });
        Assert.Null(anchor);
    }

    [Fact]
    public void Because_you_watched_anchor_uses_seed_with_most_picks()
    {
        var method = typeof(PlaylistEngine).GetMethod("SelectBecauseYouWatchedAnchor", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var seedA = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "A" };
        var seedB = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "B" };
        var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid(); var p3 = Guid.NewGuid();
        var anchor = method!.Invoke(null, new object[]
        {
            new List<Guid> { p1, p2, p3 },
            new Dictionary<Guid, MovieMetadata> { [p1] = seedA, [p2] = seedB, [p3] = seedB },
            new List<MovieMetadata> { seedA, seedB }
        });
        Assert.Same(seedB, anchor);
    }
}
