using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Services;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AIRecommender
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Register Plugin Configuration lazily, because Plugin.Instance might be null during service registration phase
            serviceCollection.AddSingleton(s => Plugin.Instance.Configuration);

            // Register Data Access
            serviceCollection.AddSingleton<MovieStore>();

            // Register AI Providers
            serviceCollection.AddSingleton<AIProviderFactory>();

            // Register Core Engines
            serviceCollection.AddSingleton<TasteProfiler>();
            serviceCollection.AddSingleton<WatchHistoryService>();
            serviceCollection.AddSingleton<SimilarityEngine>();
            serviceCollection.AddSingleton<PlaylistEngine>();
            serviceCollection.AddSingleton<LetterboxdService>();
            serviceCollection.AddSingleton<MovieClassifier>();
            serviceCollection.AddSingleton<MovieIndexer>();
        }
    }
}
