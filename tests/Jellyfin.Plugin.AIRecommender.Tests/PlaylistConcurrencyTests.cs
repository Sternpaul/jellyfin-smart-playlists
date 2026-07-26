using System.Reflection;
using Jellyfin.Plugin.AIRecommender.Services;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class PlaylistConcurrencyTests
{
    [Fact]
    public void Decay_scoring_requires_explicit_user_context()
    {
        var type = typeof(PlaylistEngine);

        Assert.Null(type.GetField("_currentUserId", BindingFlags.Instance | BindingFlags.NonPublic));

        var affinity = type.GetMethod("GetEffectiveAffinity", BindingFlags.Instance | BindingFlags.NonPublic);
        var novelty = type.GetMethod("GetNoveltyBonus", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(affinity);
        Assert.NotNull(novelty);
        Assert.Contains(affinity.GetParameters(), p => p.Name == "userId" && p.ParameterType == typeof(Guid));
        Assert.Contains(novelty.GetParameters(), p => p.Name == "userId" && p.ParameterType == typeof(Guid));
    }

    [Fact]
    public async Task Refresh_execution_gate_serializes_overlapping_operations()
    {
        var gate = new RefreshExecutionGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maxRunning = 0;

        var first = gate.RunAsync(async _ =>
        {
            var current = Interlocked.Increment(ref running);
            InterlockedExtensions.Max(ref maxRunning, current);
            firstEntered.SetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref running);
        });

        await firstEntered.Task;
        var second = gate.RunAsync(async _ =>
        {
            var current = Interlocked.Increment(ref running);
            InterlockedExtensions.Max(ref maxRunning, current);
            secondEntered.SetResult();
            await Task.Yield();
            Interlocked.Decrement(ref running);
        });

        await Task.Delay(75);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxRunning);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (current >= value)
                    return;
            }
            while (Interlocked.CompareExchange(ref location, value, current) != current);
        }
    }
}
