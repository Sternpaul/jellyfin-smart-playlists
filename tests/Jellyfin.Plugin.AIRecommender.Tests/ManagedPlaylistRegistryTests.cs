using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class ManagedPlaylistRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "airecommender-registry-tests-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "airecommender.db");

    public ManagedPlaylistRegistryTests()
    {
        Directory.CreateDirectory(_directory);
        using var db = new AiDbContext(DatabasePath);
        db.Database.EnsureCreated();
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private MovieStore CreateStore()
    {
        var store = (MovieStore)RuntimeHelpers.GetUninitializedObject(typeof(MovieStore));
        typeof(MovieStore).GetField("_dbPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, DatabasePath);
        typeof(MovieStore).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, NullLogger<MovieStore>.Instance);
        return store;
    }

    [Fact]
    public async Task Existing_database_is_upgraded_with_registry_table_in_place()
    {
        using (var db = new AiDbContext(DatabasePath))
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ManagedPlaylists");

        var store = CreateStore();
        typeof(MovieStore).GetMethod("InitializeDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, null);

        await store.UpsertManagedPlaylistAsync(
            Guid.NewGuid(),
            "for-you",
            Guid.NewGuid(),
            "For You",
            ManagedPlaylistKind.RotatingRecommendation);

        using var verify = new AiDbContext(DatabasePath);
        Assert.Equal(1, await verify.ManagedPlaylists.CountAsync());
    }

    [Fact]
    public async Task Upsert_replaces_playlist_id_without_duplicate_registration()
    {
        var userId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        var store = CreateStore();

        await store.UpsertManagedPlaylistAsync(userId, "for-you", firstId, "For You", ManagedPlaylistKind.RotatingRecommendation);
        await store.UpsertManagedPlaylistAsync(userId, "for-you", replacementId, "For You", ManagedPlaylistKind.RotatingRecommendation);

        var rows = await store.GetManagedPlaylistsAsync(userId);
        var row = Assert.Single(rows);
        Assert.Equal(replacementId, row.PlaylistId);
        Assert.Equal("for-you", row.LogicalKey);
        Assert.Equal(ManagedPlaylistKind.RotatingRecommendation, row.Kind);
    }

    [Fact]
    public async Task Exact_logical_key_lookup_returns_registered_slot()
    {
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var store = CreateStore();
        await store.UpsertManagedPlaylistAsync(userId, "dynamic:for-you", playlistId, "For You", ManagedPlaylistKind.RotatingRecommendation);

        var row = await store.GetManagedPlaylistAsync(userId, "dynamic:for-you");

        Assert.NotNull(row);
        Assert.Equal(playlistId, row!.PlaylistId);
        Assert.Null(await store.GetManagedPlaylistAsync(userId, "dynamic:recently-added"));
    }

    [Fact]
    public async Task Removing_rotating_registrations_preserves_persistent_collections()
    {
        var userId = Guid.NewGuid();
        var store = CreateStore();
        await store.UpsertManagedPlaylistAsync(userId, "for-you", Guid.NewGuid(), "For You", ManagedPlaylistKind.RotatingRecommendation);
        await store.UpsertManagedPlaylistAsync(userId, "collection:mcu", Guid.NewGuid(), "Marvel Cinematic Universe", ManagedPlaylistKind.PersistentCollection);

        await store.RemoveManagedPlaylistsAsync(userId, ManagedPlaylistKind.RotatingRecommendation);

        var row = Assert.Single(await store.GetManagedPlaylistsAsync(userId));
        Assert.Equal(ManagedPlaylistKind.PersistentCollection, row.Kind);
        Assert.Equal("collection:mcu", row.LogicalKey);
    }

    [Fact]
    public async Task Removing_one_exact_registration_preserves_other_rotating_rows()
    {
        var userId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var store = CreateStore();
        await store.UpsertManagedPlaylistAsync(userId, "for-you", firstId, "For You", ManagedPlaylistKind.RotatingRecommendation);
        await store.UpsertManagedPlaylistAsync(userId, "recently-added", secondId, "Recently Added", ManagedPlaylistKind.RotatingRecommendation);

        await store.RemoveManagedPlaylistAsync(userId, firstId);

        var remaining = Assert.Single(await store.GetManagedPlaylistsAsync(userId));
        Assert.Equal(secondId, remaining.PlaylistId);
    }

    [Fact]
    public async Task Registry_operations_are_scoped_to_one_user()
    {
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var store = CreateStore();
        await store.UpsertManagedPlaylistAsync(firstUser, "for-you", Guid.NewGuid(), "For You", ManagedPlaylistKind.RotatingRecommendation);
        await store.UpsertManagedPlaylistAsync(secondUser, "for-you", Guid.NewGuid(), "For You", ManagedPlaylistKind.RotatingRecommendation);

        await store.RemoveManagedPlaylistsAsync(firstUser, ManagedPlaylistKind.RotatingRecommendation);

        Assert.Empty(await store.GetManagedPlaylistsAsync(firstUser));
        Assert.Single(await store.GetManagedPlaylistsAsync(secondUser));
    }
}
