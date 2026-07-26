using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Tasks;

public sealed class PlaylistRefreshScheduleService : IHostedService, IDisposable
{
    private static readonly TimeSpan StartupRetryInterval = TimeSpan.FromMilliseconds(500);
    private const int StartupRetryAttempts = 60;

    private readonly ITaskManager _taskManager;
    private readonly ILogger<PlaylistRefreshScheduleService> _logger;
    private readonly object _syncLock = new();
    private Plugin? _plugin;
    private CancellationTokenSource? _syncCancellation;
    private Task? _syncTask;
    private int _configuredHours = PlaylistRefreshSchedule.DefaultHours;

    public PlaylistRefreshScheduleService(
        ITaskManager taskManager,
        ILogger<PlaylistRefreshScheduleService> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _plugin = Plugin.Instance;
        if (_plugin is null)
        {
            _logger.LogWarning("Could not synchronize playlist refresh schedule because the plugin instance is unavailable.");
            return Task.CompletedTask;
        }

        Volatile.Write(ref _configuredHours, _plugin.Configuration.PlaylistRefreshHours);
        _plugin.ConfigurationChanged += OnConfigurationChanged;
        _syncCancellation = new CancellationTokenSource();

        if (!TryApplyCurrentSchedule())
        {
            StartSynchronizationRetry();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();

        Task? syncTask;
        lock (_syncLock)
        {
            _syncCancellation?.Cancel();
            syncTask = _syncTask;
        }

        if (syncTask is not null)
        {
            try
            {
                await syncTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during host shutdown.
            }
        }
    }

    public void Dispose()
    {
        Unsubscribe();
        lock (_syncLock)
        {
            _syncCancellation?.Cancel();
            _syncCancellation?.Dispose();
            _syncCancellation = null;
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration pluginConfiguration)
        {
            return;
        }

        Volatile.Write(ref _configuredHours, pluginConfiguration.PlaylistRefreshHours);
        if (!TryApplyCurrentSchedule())
        {
            StartSynchronizationRetry();
        }
    }

    private bool TryApplyCurrentSchedule()
    {
        var workers = _taskManager.ScheduledTasks.ToArray();
        var workerExists = workers.Any(
            worker => string.Equals(
                worker.ScheduledTask.Key,
                PlaylistRefreshSchedule.ScheduledTaskKey,
                StringComparison.Ordinal));
        if (!workerExists)
        {
            return false;
        }

        var configuredHours = Volatile.Read(ref _configuredHours);
        var changed = PlaylistRefreshSchedule.ApplyToWorkers(workers, configuredHours);
        if (changed)
        {
            _logger.LogInformation(
                "Playlist refresh schedule updated to every {Hours} hour(s).",
                PlaylistRefreshSchedule.NormalizeHours(configuredHours));
        }

        return true;
    }

    private void StartSynchronizationRetry()
    {
        lock (_syncLock)
        {
            if (_syncCancellation is null || _syncCancellation.IsCancellationRequested)
            {
                return;
            }

            if (_syncTask is { IsCompleted: false })
            {
                return;
            }

            _syncTask = SynchronizeWhenWorkerAppearsAsync(_syncCancellation.Token);
        }
    }

    private async Task SynchronizeWhenWorkerAppearsAsync(CancellationToken cancellationToken)
    {
        var found = await PlaylistRefreshSchedule.WaitAndApplyAsync(
            () => _taskManager.ScheduledTasks,
            () => Volatile.Read(ref _configuredHours),
            StartupRetryInterval,
            StartupRetryAttempts,
            cancellationToken).ConfigureAwait(false);

        if (found)
        {
            _logger.LogInformation(
                "Playlist refresh schedule synchronized at startup to every {Hours} hour(s).",
                PlaylistRefreshSchedule.NormalizeHours(Volatile.Read(ref _configuredHours)));
        }
        else
        {
            _logger.LogWarning(
                "Could not find the AI Recommender playlist refresh scheduled task after waiting for task registration.");
        }
    }

    private void Unsubscribe()
    {
        if (_plugin is not null)
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
            _plugin = null;
        }
    }
}
