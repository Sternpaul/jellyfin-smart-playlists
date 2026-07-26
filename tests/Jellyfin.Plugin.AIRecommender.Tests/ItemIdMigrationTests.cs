using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class ItemIdMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "airecommender-tests-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "airecommender.db");

    public ItemIdMigrationTests()
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
        typeof(MovieStore).GetField("_saveMoviesLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, new SemaphoreSlim(1, 1));
        return store;
    }

    private static MovieMetadata Movie(Guid id, string imdb, string title = "Arrival") => new()
    {
        ItemId = id,
        ImdbId = imdb,
        Title = title,
        DateAdded = DateTime.UtcNow,
        LastUpdated = DateTime.UtcNow,
        IsClassified = true,
        Subcategories = "[\"science fiction\"]"
    };

    [Fact]
    public async Task Reassigned_item_id_migrates_all_item_references()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using (var db = new AiDbContext(DatabasePath))
        {
            db.Movies.Add(Movie(oldId, "tt2543164"));
            db.Affinities.Add(new MovieAffinity { UserId = userId.ToString(), ItemId = oldId.ToString(), Affinity = 0.7 });
            db.SurfaceHistory.Add(new SurfaceHistory { UserId = userId.ToString(), ItemId = oldId.ToString(), PlaylistType = "For You", SurfacedAt = DateTime.UtcNow });
            db.UserRatings.Add(new UserRating { UserId = userId, ItemId = oldId, Rating = 4.5, LastUpdated = DateTime.UtcNow });
            db.UserWatchlists.Add(new UserWatchlistConfig { UserId = userId, MatchedItemIds = JsonSerializer.Serialize(new[] { oldId }) });
            await db.SaveChangesAsync();
        }

        await CreateStore().SaveMoviesAsync(new[] { Movie(newId, "tt2543164", "Arrival updated") });

        using var verify = new AiDbContext(DatabasePath);
        Assert.Null(await verify.Movies.FindAsync(oldId));
        Assert.NotNull(await verify.Movies.FindAsync(newId));
        Assert.DoesNotContain(await verify.Affinities.ToListAsync(), x => Guid.Parse(x.ItemId) == oldId);
        Assert.Contains(await verify.Affinities.ToListAsync(), x => Guid.Parse(x.ItemId) == newId);
        Assert.DoesNotContain(await verify.SurfaceHistory.ToListAsync(), x => Guid.Parse(x.ItemId) == oldId);
        Assert.Contains(await verify.SurfaceHistory.ToListAsync(), x => Guid.Parse(x.ItemId) == newId);
        var ratings = await verify.UserRatings.ToListAsync();
        Assert.DoesNotContain(ratings, x => x.ItemId == oldId);
        Assert.Contains(ratings, x => x.ItemId == newId);
        var cachedIds = JsonSerializer.Deserialize<List<Guid>>((await verify.UserWatchlists.FindAsync(userId))!.MatchedItemIds!);
        Assert.DoesNotContain(oldId, cachedIds!);
        Assert.Contains(newId, cachedIds!);
    }

    [Fact]
    public async Task Lowercase_legacy_same_key_update_succeeds()
    {
        var itemId = Guid.NewGuid();
        using (var db = new AiDbContext(DatabasePath))
        {
            db.Movies.Add(Movie(itemId, "tt2543164", "Old title"));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE Movies SET ItemId = LOWER(ItemId)");
        }

        await CreateStore().SaveMoviesAsync(new[] { Movie(itemId, "tt2543164", "Updated title") });

        using var verify = new AiDbContext(DatabasePath);
        Assert.Equal("Updated title", (await verify.Movies.SingleAsync()).Title);
    }

    [Fact]
    public async Task Lowercase_legacy_item_key_migrates_without_concurrency_failure()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        using (var db = new AiDbContext(DatabasePath))
        {
            db.Movies.Add(Movie(oldId, "tt2543164"));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE Movies SET ItemId = LOWER(ItemId)");
        }

        await CreateStore().SaveMoviesAsync(new[] { Movie(newId, "tt2543164") });

        using var verify = new AiDbContext(DatabasePath);
        Assert.Single(await verify.Movies.Where(x => x.ImdbId == "tt2543164").ToListAsync());
        Assert.Contains(await verify.Movies.ToListAsync(), x => x.ItemId == newId);
    }

    [Fact]
    public void Movie_store_has_single_writer_gate()
    {
        var field = typeof(MovieStore).GetField("_saveMoviesLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(typeof(SemaphoreSlim), field!.FieldType);
    }

    [Fact]
    public async Task Concurrent_reassignment_calls_complete_without_data_loss()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        using (var db = new AiDbContext(DatabasePath))
        {
            db.Movies.Add(Movie(oldId, "tt2543164"));
            await db.SaveChangesAsync();
        }

        var store = CreateStore();
        await Task.WhenAll(
            store.SaveMoviesAsync(new[] { Movie(newId, "tt2543164", "Arrival A") }),
            store.SaveMoviesAsync(new[] { Movie(newId, "tt2543164", "Arrival B") }));

        using var verify = new AiDbContext(DatabasePath);
        Assert.Equal(1, await verify.Movies.CountAsync(x => x.ImdbId == "tt2543164"));
        Assert.NotNull(await verify.Movies.FindAsync(newId));
    }

    [Fact]
    public async Task Reassigned_item_id_rolls_back_when_replacement_insert_fails()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        using (var db = new AiDbContext(DatabasePath))
        {
            db.Movies.Add(Movie(oldId, "tt2543164"));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync($"CREATE TRIGGER reject_replacement BEFORE INSERT ON Movies WHEN LOWER(NEW.ItemId) = '{newId.ToString().ToLowerInvariant()}' BEGIN SELECT RAISE(ABORT, 'injected replacement failure'); END;");
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateStore().SaveMoviesAsync(new[] { Movie(newId, "tt2543164") }));

        using var verify = new AiDbContext(DatabasePath);
        Assert.NotNull(await verify.Movies.FindAsync(oldId));
        Assert.Null(await verify.Movies.FindAsync(newId));
    }
}
