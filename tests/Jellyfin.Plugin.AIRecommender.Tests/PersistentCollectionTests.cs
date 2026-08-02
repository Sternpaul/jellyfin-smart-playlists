using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services.Collections;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class PersistentCollectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "airecommender-collection-tests-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "airecommender.db");

    public PersistentCollectionTests()
    {
        Directory.CreateDirectory(_directory);
        using var db = new AiDbContext(DatabasePath);
        db.Database.EnsureCreated();
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task Definition_and_per_user_assignment_persist_across_contexts()
    {
        var store = CreateStore();
        var definition = new CollectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "My Universe",
            Type = CollectionDefinitionType.CuratedUniverse,
            TmdbMovieIdsJson = "[11,22]",
            ImdbIdsJson = "[\"tt001\"]"
        };
        var userId = Guid.NewGuid();

        await store.SaveCollectionDefinitionAsync(definition);
        await store.SetCollectionSubscriptionAsync(userId, definition.Id, assigned: true);

        var saved = Assert.Single(await CreateStore().GetCollectionDefinitionsForUserAsync(userId));
        Assert.Equal(definition.Id, saved.Id);
        Assert.Equal("My Universe", saved.Name);
    }

    [Fact]
    public async Task Collection_definition_names_are_unique_ignoring_case_at_database_boundary()
    {
        var store = CreateStore();
        await store.SaveCollectionDefinitionAsync(new CollectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Curated Saga"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveCollectionDefinitionAsync(new CollectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "curated saga"
        }));
    }

    [Fact]
    public async Task Existing_database_is_upgraded_with_collection_tables_in_place()
    {
        using (var db = new AiDbContext(DatabasePath))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE UserCollectionSubscriptions");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE CollectionDefinitions");
        }

        var store = CreateStore();
        typeof(MovieStore).GetMethod("InitializeDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null);
        typeof(MovieStore).GetMethod("InitializeDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null);
        var definitionId = Guid.NewGuid();
        await store.SaveCollectionDefinitionAsync(new CollectionDefinition { Id = definitionId, Name = "Restored" });

        Assert.Equal(definitionId, Assert.Single(await CreateStore().GetCollectionDefinitionsAsync()).Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => CreateStore().SaveCollectionDefinitionAsync(
            new CollectionDefinition { Id = Guid.NewGuid(), Name = "restored" }));
    }

    [Fact]
    public void Collection_definition_type_serializes_as_dashboard_string_value()
    {
        Assert.Equal("\"ExplicitMovies\"", JsonSerializer.Serialize(CollectionDefinitionType.ExplicitMovies));
        Assert.Equal("\"CuratedUniverse\"", JsonSerializer.Serialize(CollectionDefinitionType.CuratedUniverse));
    }

    [Fact]
    public async Task Unassigning_one_user_preserves_another_users_assignment()
    {
        var store = CreateStore();
        var definition = new CollectionDefinition { Id = Guid.NewGuid(), Name = "Shared" };
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await store.SaveCollectionDefinitionAsync(definition);
        await store.SetCollectionSubscriptionAsync(first, definition.Id, true);
        await store.SetCollectionSubscriptionAsync(second, definition.Id, true);

        await store.SetCollectionSubscriptionAsync(first, definition.Id, false);

        Assert.Empty(await store.GetCollectionDefinitionsForUserAsync(first));
        Assert.Single(await store.GetCollectionDefinitionsForUserAsync(second));
    }

    [Fact]
    public async Task Deleting_a_definition_removes_all_subscriptions_without_orphans()
    {
        var store = CreateStore();
        var definition = new CollectionDefinition { Id = Guid.NewGuid(), Name = "Temporary" };
        await store.SaveCollectionDefinitionAsync(definition);
        await store.SetCollectionSubscriptionAsync(Guid.NewGuid(), definition.Id, true);
        await store.SetCollectionSubscriptionAsync(Guid.NewGuid(), definition.Id, true);

        await store.DeleteCollectionDefinitionAsync(definition.Id);

        using var db = new AiDbContext(DatabasePath);
        Assert.Empty(await db.CollectionDefinitions.ToListAsync());
        Assert.Empty(await db.UserCollectionSubscriptions.ToListAsync());
    }

    [Fact]
    public void Resolver_matches_tmdb_first_then_imdb_and_orders_by_release_year()
    {
        var tmdb = Movie("Later", 2020, 22, "tt022");
        var imdb = Movie("Earlier", 1999, 99, "tt001");
        var unrelated = Movie("Other", 1980, 77, "tt077");
        var definition = new CollectionDefinition
        {
            TmdbMovieIdsJson = "[22]",
            ImdbIdsJson = "[\"TT001\",\"tt022\"]"
        };

        var resolved = CollectionResolver.Resolve(definition, new[] { tmdb, imdb, unrelated });

        Assert.Equal(new[] { imdb.ItemId, tmdb.ItemId }, resolved.Select(movie => movie.ItemId));
    }

    [Fact]
    public void Resolver_ignores_invalid_json_and_duplicate_identifiers()
    {
        var movie = Movie("One", 2000, 11, "tt011");
        var definition = new CollectionDefinition { TmdbMovieIdsJson = "[11,11]", ImdbIdsJson = "not-json" };

        Assert.Equal(new[] { movie.ItemId }, CollectionResolver.Resolve(definition, new[] { movie }).Select(x => x.ItemId));
    }

    [Fact]
    public void Policy_uses_definition_identity_and_isolates_learning_and_cleanup()
    {
        var definitionId = Guid.NewGuid();
        var persistent = new ManagedPlaylist
        {
            LogicalKey = PersistentCollectionPolicy.LogicalKey(definitionId),
            Kind = ManagedPlaylistKind.PersistentCollection
        };
        var rotating = new ManagedPlaylist
        {
            LogicalKey = "dynamic:for-you",
            Kind = ManagedPlaylistKind.RotatingRecommendation
        };
        var unrelatedPersistent = new ManagedPlaylist
        {
            LogicalKey = "future:persistent-surface",
            Kind = ManagedPlaylistKind.PersistentCollection
        };

        Assert.Equal($"collection:{definitionId:N}", persistent.LogicalKey);
        Assert.True(PersistentCollectionPolicy.IsOwnedCollectionRegistration(persistent));
        Assert.False(PersistentCollectionPolicy.IsOwnedCollectionRegistration(unrelatedPersistent));
        Assert.False(PersistentCollectionPolicy.IsOwnedCollectionRegistration(rotating));
    }

    [Fact]
    public void Persistent_description_uses_the_administrator_reason_without_operational_details()
    {
        var description = PlaylistDescriptionBuilder.BuildPersistentCollection(
            "Saga",
            "Release-order viewing.");

        Assert.Equal("Release-order viewing.", description);
        Assert.DoesNotContain("rotate", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contains", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unwatched", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Learning_keeps_owner_scoped_personal_and_rotating_playlists_but_excludes_persistent_ids()
    {
        var userId = Guid.NewGuid();
        var personalId = Guid.NewGuid();
        var rotatingId = Guid.NewGuid();
        var persistentId = Guid.NewGuid();
        IReadOnlySet<Guid> excluded = new HashSet<Guid> { persistentId };

        Assert.True(PersistentCollectionPolicy.ShouldUsePlaylistForLearning(userId, personalId, userId, excluded));
        Assert.True(PersistentCollectionPolicy.ShouldUsePlaylistForLearning(userId, rotatingId, userId, excluded));
        Assert.False(PersistentCollectionPolicy.ShouldUsePlaylistForLearning(userId, persistentId, userId, excluded));
        Assert.False(PersistentCollectionPolicy.ShouldUsePlaylistForLearning(Guid.NewGuid(), personalId, userId, excluded));
    }

    private MovieStore CreateStore()
    {
        var store = (MovieStore)RuntimeHelpers.GetUninitializedObject(typeof(MovieStore));
        typeof(MovieStore).GetField("_dbPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, DatabasePath);
        typeof(MovieStore).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, NullLogger<MovieStore>.Instance);
        return store;
    }

    private static MovieMetadata Movie(string title, int year, int tmdb, string imdb) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        ReleaseYear = year,
        TmdbId = tmdb,
        ImdbId = imdb
    };
}
