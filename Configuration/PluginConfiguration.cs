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

        // Dynamic Rating / Learning (v1.3.0) — all SMALL nudges, fully configurable.
        public int AffinityDecayHalfLifeDays { get; set; } = 28;   // days; half-life for affinity/penalty decay (lazy, at read)
        public double PunishmentPenalty { get; set; } = -0.30;        // affinity drop for siblings of a watched movie
        public double RewardBoost { get; set; } = 0.10;            // affinity rise for movies similar to a watched movie
        public double AffinityRankWeight { get; set; } = 0.15;     // max contribution of affinity to a 0..1 ranking score
        public int NewMovieBoostDays { get; set; } = 30;           // window (days) a fresh movie gets the recency nudge
        public double NewMovieBoostWeight { get; set; } = 0.10;     // size of the recency nudge (capped by AffinityRankWeight)

        // User Exclusions
        // User GUIDs (as strings) for whom playlist generation is skipped entirely.
        public List<string> DisabledUserIds { get; set; } = new();
    }
}
