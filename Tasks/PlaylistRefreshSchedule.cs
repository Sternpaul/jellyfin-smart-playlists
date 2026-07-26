using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AIRecommender.Tasks;

public static class PlaylistRefreshSchedule
{
    public const string ScheduledTaskKey = "AIRecommenderRefreshPlaylists";
    public const int DefaultHours = 12;
    public const int MinimumHours = 1;
    public const int MaximumHours = 168;

    public static int NormalizeHours(int configuredHours)
        => configuredHours is >= MinimumHours and <= MaximumHours
            ? configuredHours
            : DefaultHours;

    public static IReadOnlyList<TaskTriggerInfo> CreateTriggers(int configuredHours)
        => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(NormalizeHours(configuredHours)).Ticks
            }
        };

    public static bool ApplyToWorkers(
        IEnumerable<IScheduledTaskWorker> workers,
        int configuredHours)
    {
        ArgumentNullException.ThrowIfNull(workers);

        var worker = workers.FirstOrDefault(
            candidate => string.Equals(
                candidate.ScheduledTask.Key,
                ScheduledTaskKey,
                StringComparison.Ordinal));
        if (worker is null)
        {
            return false;
        }

        var desired = CreateTriggers(configuredHours);
        if (worker.Triggers.Count == 1
            && worker.Triggers[0].Type == TaskTriggerInfoType.IntervalTrigger
            && worker.Triggers[0].IntervalTicks == desired[0].IntervalTicks)
        {
            return false;
        }

        worker.Triggers = desired;
        return true;
    }

    public static async Task<bool> WaitAndApplyAsync(
        Func<IEnumerable<IScheduledTaskWorker>> workerProvider,
        Func<int> configuredHoursProvider,
        TimeSpan retryInterval,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workerProvider);
        ArgumentNullException.ThrowIfNull(configuredHoursProvider);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workers = workerProvider().ToArray();
            var exists = workers.Any(
                worker => string.Equals(
                    worker.ScheduledTask.Key,
                    ScheduledTaskKey,
                    StringComparison.Ordinal));
            if (exists)
            {
                ApplyToWorkers(workers, configuredHoursProvider());
                return true;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
