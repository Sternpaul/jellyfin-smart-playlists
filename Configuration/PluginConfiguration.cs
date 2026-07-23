using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AIRecommender.Configuration
{
    public enum AIProviderType
    {
        GoogleAI,
        OpenRouter,
        OpenAI,
        Anthropic
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // AI Provider Settings
        public AIProviderType AIProvider { get; set; } = AIProviderType.OpenRouter;
        public string ApiKey { get; set; } = string.Empty;
        public string ClassificationModel { get; set; } = "nvidia/nemotron-3-super-120b-a12b:free";
        public string ChatModel { get; set; } = "nvidia/nemotron-3-super-120b-a12b:free";
        public string CustomEndpoint { get; set; } = string.Empty;

        // Playlist Settings
        public int PlaylistRefreshHours { get; set; } = 12;
        public int MaxMoviesPerPlaylist { get; set; } = 20;
        public int PlaylistRotationPercent { get; set; } = 30;
        public int DiversityWeight { get; set; } = 25; // percentage, 10-50
        public int CoolingPeriodCycles { get; set; } = 2;
        
        // Playlist Toggles
        public bool EnableForYou { get; set; } = true;
        public bool EnableBecauseYouWatched { get; set; } = true;
        public bool EnableHiddenGems { get; set; } = true;
        public bool EnableRecentlyAdded { get; set; } = true;
        public bool EnableSubcategory { get; set; } = true;
        public bool EnableDiscover { get; set; } = true;
        public bool EnableWildCard { get; set; } = true;
        
        // Taste Profile Settings
        public int TasteDecayHalfLifeDays { get; set; } = 120; // days; exponential decay of older watches in the taste profile
        public int ReviewNudgingWeight { get; set; } = 0; // percentage, 0-15
    }
}
