using System.Reflection;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Jellyfin.Plugin.AIRecommender.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class VerifiedWatchPolicyTests
{
    private static double? Evaluate(UserDataSaveReason reason, bool played, long positionTicks, long runtimeTicks)
    {
        var method = typeof(WatchHistoryService).GetMethod("GetVerifiedPlaybackPercentage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (double?)method!.Invoke(null, new object[] { reason, played, positionTicks, runtimeTicks });
    }

    [Fact]
    public void Completion_threshold_is_not_administrator_configurable()
    {
        Assert.Null(typeof(PluginConfiguration).GetProperty("MinCompletionPercent"));
    }

    [Fact]
    public void Manual_mark_played_is_not_a_verified_watch()
    {
        Assert.Null(Evaluate(UserDataSaveReason.TogglePlayed, true, 0, TimeSpan.FromHours(2).Ticks));
    }

    [Theory]
    [InlineData(49.9, false)]
    [InlineData(50.0, false)]
    [InlineData(50.1, true)]
    [InlineData(80.0, true)]
    public void Finished_jellyfin_playback_requires_strictly_more_than_fifty_percent(double percent, bool expected)
    {
        var runtime = TimeSpan.FromHours(2).Ticks;
        var result = Evaluate(UserDataSaveReason.PlaybackFinished, false, (long)(runtime * percent / 100.0), runtime);
        Assert.Equal(expected, result.HasValue);
    }

    [Fact]
    public void Reset_position_is_not_verified_without_direct_over_fifty_percent_evidence()
    {
        Assert.Null(Evaluate(UserDataSaveReason.PlaybackFinished, true, 0, TimeSpan.FromHours(2).Ticks));
    }

    [Fact]
    public void Playback_progress_save_does_not_emit_duplicate_watch_signals()
    {
        Assert.Null(Evaluate(UserDataSaveReason.PlaybackProgress, false, TimeSpan.FromMinutes(80).Ticks, TimeSpan.FromMinutes(100).Ticks));
    }
}
