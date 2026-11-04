using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using Jellyfin.Plugin.AIRecommender.Services.Classification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class ClassificationVocabularyTests
{
    [Theory]
    [InlineData("Psychological Thrillers", "Psychological Thriller")]
    [InlineData("psychological-thriller", "Psychological Thriller")]
    [InlineData("hard scifi", "Hard Sci-Fi")]
    [InlineData("Hard Sci Fi", "Hard Sci-Fi")]
    [InlineData("coming of age", "Coming-of-Age")]
    [InlineData("Black Comedy", "Dark Comedy")]
    [InlineData("Espionage", "Spy")]
    [InlineData("Sword and Sorcery", "Sword & Sorcery")]
    [InlineData("heist film", "Heist")]
    [InlineData("Sports", "Sports Drama")]
    [InlineData("Christmas", "Christmas")]
    public void Subcategory_variants_fold_to_one_canonical_name(string raw, string expected)
        => Assert.Equal(expected, ClassificationVocabulary.CanonicalSubcategory(raw));

    [Theory]
    [InlineData("Action")]
    [InlineData("Drama")]
    [InlineData("Science Fiction")]
    [InlineData("")]
    public void Broad_genres_are_not_subcategories(string raw)
        => Assert.Null(ClassificationVocabulary.CanonicalSubcategory(raw));

    [Fact]
    public void Every_canonical_value_maps_to_itself()
    {
        foreach (var value in ClassificationVocabulary.Subcategories)
            Assert.Equal(value, ClassificationVocabulary.CanonicalSubcategory(value));
        foreach (var value in ClassificationVocabulary.Moods)
            Assert.Equal(value, ClassificationVocabulary.CanonicalMood(value));
        foreach (var value in ClassificationVocabulary.NarrativeStyles)
            Assert.Equal(value, ClassificationVocabulary.CanonicalNarrativeStyle(value));
    }

    [Fact]
    public void Canonicalize_dedupes_and_respects_keep_unknown()
    {
        var raw = new[] { "Heist", "heists", "Crime", "Neo Noir" };
        Assert.Equal(new[] { "Heist", "Neo-Noir" },
            ClassificationVocabulary.Canonicalize(raw, ClassificationVocabulary.CanonicalSubcategory, keepUnknown: false));
        Assert.Equal(new[] { "Heist", "Crime", "Neo-Noir" },
            ClassificationVocabulary.Canonicalize(raw, ClassificationVocabulary.CanonicalSubcategory, keepUnknown: true));
    }

    [Fact]
    public void Prompt_lists_the_vocabulary()
    {
        var prompt = ClassificationPrompt.Build(new[] { new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Heat" } });
        Assert.Contains("\"Neo-Noir\"", prompt);
        Assert.Contains("\"slow-burn\"", prompt);
        Assert.Contains("Heat", prompt);
    }

    [Fact]
    public void Classifier_maps_ai_output_onto_vocabulary_and_marks_version()
    {
        var classifier = new MovieClassifier(null!, null!, NullLogger<MovieClassifier>.Instance);
        var movie = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Heat" };

        classifier.ApplyClassification(movie,
            new[] { "Heist Thriller", "Crime", "Neo Noir", "Gangster", "Action Thriller" },
            new[] { "Tense", "gritty", "made-up mood" },
            new[] { "Obsession", "obsession", "Professionalism" },
            "Ensemble Cast", "popular", "Very High", 9);

        Assert.True(movie.IsClassified);
        Assert.Equal(ClassificationVocabulary.Version, movie.VocabularyVersion);
        Assert.Equal(new[] { "Heist", "Neo-Noir", "Gangster" }, movie.SubcategorySet.Items);
        Assert.Equal(new[] { "tense", "gritty" }, movie.MoodSet.Items);
        Assert.Equal(new[] { "obsession", "professionalism" }, movie.ThemeSet.Items);
        Assert.Equal("ensemble", movie.NarrativeStyle);
        Assert.Equal("mainstream", movie.Accessibility);
        Assert.Equal("high", movie.Intensity);
        Assert.Equal(9, movie.CriticalAcclaimScore);
    }

    [Fact]
    public void Classifier_keeps_off_vocabulary_output_as_legacy_rather_than_losing_the_movie()
    {
        var classifier = new MovieClassifier(null!, null!, NullLogger<MovieClassifier>.Instance);
        var movie = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Oldboy" };

        classifier.ApplyClassification(movie, new[] { "Action", "Drama" }, Array.Empty<string>(), Array.Empty<string>(), null, null, null, 42);

        Assert.True(movie.IsClassified);
        Assert.Equal(0, movie.VocabularyVersion);
        Assert.Equal(new[] { "Action", "Drama" }, movie.SubcategorySet.Items);
        Assert.Equal(0, movie.CriticalAcclaimScore); // out-of-range score ignored
    }

    [Fact]
    public void Legacy_rows_are_folded_in_place_without_losing_unknown_tags()
    {
        var directory = Path.Combine(Path.GetTempPath(), "airecommender-vocab-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "airecommender.db");
            var legacyId = Guid.NewGuid();
            var currentId = Guid.NewGuid();
            using (var db = new AiDbContext(path))
            {
                db.Database.EnsureCreated();
                db.Movies.Add(new MovieMetadata
                {
                    ItemId = legacyId, Title = "Legacy", IsClassified = true,
                    Subcategories = "[\"Psychological Thrillers\",\"Sci-Fi\"]", Moods = "[\"Suspense\"]",
                    NarrativeStyle = "Non Linear", DateAdded = DateTime.UtcNow, LastUpdated = DateTime.UtcNow,
                });
                db.Movies.Add(new MovieMetadata
                {
                    ItemId = currentId, Title = "Current", IsClassified = true, VocabularyVersion = ClassificationVocabulary.Version,
                    Subcategories = "[\"Sci-Fi\"]", DateAdded = DateTime.UtcNow, LastUpdated = DateTime.UtcNow,
                });
                db.SaveChanges();
                // 2.0.0 lowercased stored ids at startup, while EF keys its updates on
                // the uppercase form. The fold must find the row in either casing.
                db.Database.ExecuteSqlRaw("UPDATE Movies SET ItemId = LOWER(ItemId)");
            }

            using (var db = new AiDbContext(path))
                Assert.Equal(1, MovieStore.NormalizeLegacyClassifications(db));
            using (var db = new AiDbContext(path))
            {
                var legacy = db.Movies.AsEnumerable().Single(m => m.ItemId == legacyId);
                Assert.Equal(new[] { "Psychological Thriller", "Sci-Fi" }, legacy.SubcategorySet.Items);
                Assert.Equal(new[] { "suspenseful" }, legacy.MoodSet.Items);
                Assert.Equal("nonlinear", legacy.NarrativeStyle);
                Assert.Equal(0, legacy.VocabularyVersion);
                Assert.Equal("[\"Sci-Fi\"]", db.Movies.AsEnumerable().Single(m => m.ItemId == currentId).Subcategories);
                // Second run is a no-op.
                Assert.Equal(0, MovieStore.NormalizeLegacyClassifications(db));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}

public sealed class RecommendationSignalTests
{
    [Fact]
    public void Acclaim_prefers_jellyfin_ratings_over_the_ai_guess()
    {
        Assert.Equal(6, new MovieMetadata { CriticalAcclaimScore = 6 }.Acclaim);
        Assert.Equal(7.5, new MovieMetadata { CriticalAcclaimScore = 3, CommunityRating = 7.5f }.Acclaim, 3);
        Assert.Equal(9.0, new MovieMetadata { CriticalAcclaimScore = 3, CriticRating = 90 }.Acclaim, 3);
        Assert.Equal(8.0, new MovieMetadata { CommunityRating = 7f, CriticRating = 90 }.Acclaim, 3);
        // Out-of-range provider values are ignored.
        Assert.Equal(5, new MovieMetadata { CriticalAcclaimScore = 5, CommunityRating = 0f, CriticRating = 250 }.Acclaim);
    }

    [Fact]
    public void Tag_set_is_case_insensitive_and_cache_follows_the_json()
    {
        var movie = new MovieMetadata { Subcategories = "[\"Heist\",\"heist\",\"Spy\"]" };
        Assert.Equal(new[] { "Heist", "Spy" }, movie.SubcategorySet.Items);
        var other = new MovieMetadata { Subcategories = JsonSerializer.Serialize(new[] { "SPY", "Western" }) };
        Assert.Equal(1.0 / 3.0, movie.SubcategorySet.Jaccard(other.SubcategorySet), 6);

        movie.Subcategories = "not json";
        Assert.Equal(0, movie.SubcategorySet.Count);
    }

    [Theory]
    [InlineData(0.0, -0.30)]
    [InlineData(0.5, -0.18)]
    [InlineData(1.0, -0.06)]
    public void Sibling_penalty_shrinks_for_lookalikes(double similarity, double expected)
        => Assert.Equal(expected, PlaylistEngine.SiblingPenalty(-0.30, similarity), 6);
}
