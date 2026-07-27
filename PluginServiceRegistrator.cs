using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Services;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using Jellyfin.Plugin.AIRecommender.Services.Playlists;
using Jellyfin.Plugin.AIRecommender.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Plugin Configuration is accessed dynamically via Plugin.Instance.Configuration to support hot-reloading

            // Register Data Access
            serviceCollection.AddSingleton<MovieStore>();

            serviceCollection.AddHttpClient<LetterboxdService>();
            serviceCollection.AddSingleton<TasteProfiler>();
            serviceCollection.AddSingleton<TmdbKeywordService>(sp =>
            {
                var ms = sp.GetRequiredService<MovieStore>();
                var http = sp.GetRequiredService<HttpClient>();
                var logger = sp.GetRequiredService<ILogger<TmdbKeywordService>>();
                return new TmdbKeywordService(http, ms, logger, ms.DataDirectory);
            });

            // Register AI Providers
            serviceCollection.AddSingleton<AIProviderFactory>();
            serviceCollection.AddSingleton<WatchHistoryService>();
            serviceCollection.AddSingleton<SimilarityEngine>();
            serviceCollection.AddSingleton<PlaylistEngine>();
            serviceCollection.AddSingleton<PlaylistArtworkService>();
            serviceCollection.AddSingleton<LetterboxdService>();
            serviceCollection.AddSingleton<MovieClassifier>();
            serviceCollection.AddSingleton<MovieIndexer>();
            serviceCollection.AddHostedService<PlaylistRefreshScheduleService>();
        }
    }
}
