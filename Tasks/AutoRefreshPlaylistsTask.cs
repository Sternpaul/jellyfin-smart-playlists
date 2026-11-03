using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Tasks
{
    public class AutoRefreshPlaylistsTask : IScheduledTask
    {
        private readonly IUserManager _userManager;
        private readonly PlaylistEngine _playlistEngine;
        private readonly ILogger<AutoRefreshPlaylistsTask> _logger;

        public AutoRefreshPlaylistsTask(IUserManager userManager, PlaylistEngine playlistEngine, ILogger<AutoRefreshPlaylistsTask> logger)
        {
            _userManager = userManager;
            _playlistEngine = playlistEngine;
            _logger = logger;
        }

        public string Name => "AI Recommender - Refresh Playlists";
        public string Key => PlaylistRefreshSchedule.ScheduledTaskKey;
        public string Description => "Refreshes AI smart playlists for all users, cycling out old movies and applying punishments.";
        public string Category => "AI Recommender";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var configuredHours = Plugin.Instance?.Configuration?.PlaylistRefreshHours
                ?? PlaylistRefreshSchedule.DefaultHours;
            return PlaylistRefreshSchedule.CreateTriggers(configuredHours);
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var users = _userManager.GetUsers().ToArray();
            var failures = new List<Exception>();

            for (int i = 0; i < users.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var user = users[i];

                // One user's failure (bad ratings URL, a deleted item, a Jellyfin API
                // change) must not stop everyone else's playlists from refreshing.
                try
                {
                    await _playlistEngine.RefreshUserPlaylistsAsync(user.Id, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Refreshing playlists for user {UserName} ({UserId}) failed; continuing with the next user.", user.Username, user.Id);
                    failures.Add(ex);
                }

                progress.Report((double)(i + 1) / users.Length * 100);
            }

            // Still surface the failure in Dashboard > Scheduled Tasks.
            if (failures.Count > 0)
                throw new AggregateException(
                    $"Playlist refresh failed for {failures.Count} of {users.Length} users; see the log for details.",
                    failures);
        }
    }
}
