using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AIRecommender.Tasks
{
    public class AutoRefreshPlaylistsTask : IScheduledTask
    {
        private readonly IUserManager _userManager;
        private readonly PlaylistEngine _playlistEngine;

        public AutoRefreshPlaylistsTask(IUserManager userManager, PlaylistEngine playlistEngine)
        {
            _userManager = userManager;
            _playlistEngine = playlistEngine;
        }

        public string Name => "AI Recommender - Refresh Playlists";
        public string Key => "AIRecommenderRefreshPlaylists";
        public string Description => "Refreshes AI smart playlists for all users, cycling out old movies and applying punishments.";
        public string Category => "AI Recommender";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(12).Ticks }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var users = System.Linq.Enumerable.ToArray(_userManager.GetUsers());
            int total = users.Length;
            int current = 0;

            foreach (var user in users)
            {
                if (cancellationToken.IsCancellationRequested) break;
                
                var userId = (Guid)user.GetType().GetProperty("Id").GetValue(user);
                await _playlistEngine.RefreshUserPlaylistsAsync(userId, cancellationToken);
                
                current++;
                progress.Report((double)current / total * 100);
            }
        }
    }
}
