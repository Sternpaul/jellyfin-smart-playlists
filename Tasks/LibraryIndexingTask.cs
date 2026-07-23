using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AIRecommender.Tasks
{
    public class LibraryIndexingTask : IScheduledTask
    {
        private readonly MovieIndexer _movieIndexer;
        private readonly MovieClassifier _movieClassifier;

        public LibraryIndexingTask(MovieIndexer movieIndexer, MovieClassifier movieClassifier)
        {
            _movieIndexer = movieIndexer;
            _movieClassifier = movieClassifier;
        }

        public string Name => "AI Recommender - Index & Classify Library";
        public string Key => "AIRecommenderIndexLibrary";
        public string Description => "Scans the library for new movies and batches them to the AI for classification.";
        public string Category => "AI Recommender";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress.Report(0);
            
            // Step 1: Scan Jellyfin library to sync basic metadata
            await _movieIndexer.IndexLibraryAsync(cancellationToken);
            progress.Report(50);
            
            // Step 2: Batch unclassified movies to AI provider
            await _movieClassifier.ClassifyPendingMoviesAsync(cancellationToken);
            progress.Report(100);
        }
    }
}
