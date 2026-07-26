using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.AIRecommender.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class PlaylistRefreshScheduleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(168)]
    public void Configured_hours_produce_one_interval_trigger(int hours)
    {
        var triggers = PlaylistRefreshSchedule.CreateTriggers(hours);

        var trigger = Assert.Single(triggers);
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(hours).Ticks, trigger.IntervalTicks);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(169)]
    public void Invalid_hours_use_safe_twelve_hour_default(int hours)
    {
        var trigger = Assert.Single(PlaylistRefreshSchedule.CreateTriggers(hours));

        Assert.Equal(TimeSpan.FromHours(12).Ticks, trigger.IntervalTicks);
    }

    [Fact]
    public void Applying_schedule_updates_only_refresh_worker_and_skips_unchanged_value()
    {
        var target = new FakeWorker(
            PlaylistRefreshSchedule.ScheduledTaskKey,
            PlaylistRefreshSchedule.CreateTriggers(12));
        var unrelated = new FakeWorker("SomeOtherTask", PlaylistRefreshSchedule.CreateTriggers(24));

        Assert.True(PlaylistRefreshSchedule.ApplyToWorkers(new[] { target, unrelated }, 6));
        Assert.Equal(TimeSpan.FromHours(6).Ticks, Assert.Single(target.Triggers).IntervalTicks);
        Assert.Equal(1, target.TriggerSetCount);
        Assert.Equal(TimeSpan.FromHours(24).Ticks, Assert.Single(unrelated.Triggers).IntervalTicks);
        Assert.Equal(0, unrelated.TriggerSetCount);

        Assert.False(PlaylistRefreshSchedule.ApplyToWorkers(new[] { target, unrelated }, 6));
        Assert.Equal(1, target.TriggerSetCount);
    }

    [Fact]
    public async Task Startup_sync_retries_until_refresh_worker_is_registered()
    {
        IReadOnlyList<IScheduledTaskWorker> workers = Array.Empty<IScheduledTaskWorker>();
        var target = new FakeWorker(
            PlaylistRefreshSchedule.ScheduledTaskKey,
            PlaylistRefreshSchedule.CreateTriggers(12));

        var sync = PlaylistRefreshSchedule.WaitAndApplyAsync(
            () => workers,
            () => 6,
            TimeSpan.FromMilliseconds(10),
            maxAttempts: 10,
            CancellationToken.None);

        await Task.Delay(25);
        workers = new[] { target };

        Assert.True(await sync);
        Assert.Equal(TimeSpan.FromHours(6).Ticks, Assert.Single(target.Triggers).IntervalTicks);
        Assert.Equal(1, target.TriggerSetCount);
    }

    [Fact]
    public void Schedule_synchronizer_is_registered_as_hosted_service()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(PlaylistRefreshScheduleService));
    }

    private sealed class FakeWorker : IScheduledTaskWorker
    {
        private IReadOnlyList<TaskTriggerInfo> _triggers;

        public FakeWorker(string key, IReadOnlyList<TaskTriggerInfo> triggers)
        {
            ScheduledTask = new FakeTask(key);
            _triggers = triggers;
        }

        public event EventHandler<GenericEventArgs<double>>? TaskProgress
        {
            add { }
            remove { }
        }
        public IScheduledTask ScheduledTask { get; }
        public TaskResult? LastExecutionResult => null;
        public string Name => ScheduledTask.Name;
        public string Description => string.Empty;
        public string Category => "Tests";
        public TaskState State => TaskState.Idle;
        public double? CurrentProgress => null;
        public int TriggerSetCount { get; private set; }
        public IReadOnlyList<TaskTriggerInfo> Triggers
        {
            get => _triggers;
            set
            {
                TriggerSetCount++;
                _triggers = value;
            }
        }

        public string Id => "test-worker";
        public void ReloadTriggerEvents() { }
        public void Dispose() { }
    }

    private sealed class FakeTask : IScheduledTask
    {
        public FakeTask(string key) => Key = key;
        public string Name => Key;
        public string Key { get; }
        public string Description => string.Empty;
        public string Category => "Tests";
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => Array.Empty<TaskTriggerInfo>();
    }
}
