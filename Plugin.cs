using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AIRecommender.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AIRecommender
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "AI Recommender";
        public override Guid Id => Guid.Parse("3D3D8BE7-67AB-4F65-9F31-3EAE8764BBA3");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "AIRecommenderConfig",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }
}
