using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.AIRecommender.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public sealed class VerifiedWatchStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "airecommender-watch-tests-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "airecommender.db");

    public VerifiedWatchStoreTests()
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
        typeof(MovieStore).GetField("_verifiedWatchLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(store, new SemaphoreSlim(1, 1));
        return store;
    }

    [Fact]
    public async Task Verified_watch_dates_are_persistent_user_scoped_and_keep_latest_watch()
    {
        var store = CreateStore();
        var record = typeof(MovieStore).GetMethod("RecordVerifiedWatchAsync");
        var read = typeof(MovieStore).GetMethod("GetVerifiedWatchDatesAsync");
        Assert.NotNull(record);
        Assert.NotNull(read);

        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var item = Guid.NewGuid();
        var first = DateTime.UtcNow.AddDays(-2);
        var latest = DateTime.UtcNow.AddDays(-1);

        await (Task)record!.Invoke(store, new object[] { user, item, first, 60.0, CancellationToken.None })!;
        await (Task)record.Invoke(store, new object[] { user, item, latest, 80.0, CancellationToken.None })!;
        await (Task)record.Invoke(store, new object[] { otherUser, item, DateTime.UtcNow, 90.0, CancellationToken.None })!;

        var readTask = (Task)read!.Invoke(CreateStore(), new object[] { user, CancellationToken.None })!;
        await readTask;
        var result = Assert.IsAssignableFrom<IDictionary<Guid, DateTime>>(
            readTask.GetType().GetProperty("Result")!.GetValue(readTask));

        Assert.Single(result);
        Assert.Equal(latest, result[item], TimeSpan.FromMilliseconds(1));
    }
}
