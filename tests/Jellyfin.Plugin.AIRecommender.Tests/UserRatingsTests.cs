using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class UserRatingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ai-recommender-rating-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "ratings.db");

    public UserRatingsTests()
    {
        Directory.CreateDirectory(_directory);
        using var db = new AiDbContext(DatabasePath);
        db.Database.EnsureCreated();
    }

    private MovieStore CreateStore()
    {
        var store = (MovieStore)RuntimeHelpers.GetUninitializedObject(typeof(MovieStore));
        typeof(MovieStore).GetField("_dbPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, DatabasePath);
        typeof(MovieStore).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, NullLogger<MovieStore>.Instance);
        typeof(MovieStore).GetField("_saveRatingsLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, new SemaphoreSlim(1, 1));
        return store;
    }

    private static UserRating Rating(Guid userId, Guid itemId, double value) => new()
    {
        UserId = userId,
        ItemId = itemId,
        Rating = value,
        SourceTitle = "Fixture",
        LastUpdated = DateTime.UtcNow
    };

    [Fact]
    public async Task Duplicate_import_rows_are_collapsed_with_last_value_winning()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await CreateStore().SaveUserRatingsAsync(userId, new[]
        {
            Rating(userId, itemId, 3.0),
            Rating(userId, itemId, 4.5)
        });

        using var verify = new AiDbContext(DatabasePath);
        var row = Assert.Single(await verify.UserRatings.ToListAsync());
        Assert.Equal(4.5, row.Rating);
    }

    [Fact]
    public async Task Failed_replacement_rolls_back_and_preserves_previous_ratings()
    {
        var userId = Guid.NewGuid();
        var oldItemId = Guid.NewGuid();
        var newItemId = Guid.NewGuid();
        using (var db = new AiDbContext(DatabasePath))
        {
            db.UserRatings.Add(Rating(userId, oldItemId, 4.0));
            await db.SaveChangesAsync();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_rating BEFORE INSERT ON UserRatings BEGIN SELECT RAISE(ABORT, 'injected rating failure'); END;");
#pragma warning restore EF1002
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateStore().SaveUserRatingsAsync(userId, new[] { Rating(userId, newItemId, 5.0) }));

        using var verify = new AiDbContext(DatabasePath);
        var row = Assert.Single(await verify.UserRatings.ToListAsync());
        Assert.Equal(oldItemId, row.ItemId);
        Assert.Equal(4.0, row.Rating);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
