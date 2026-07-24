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

        // v1.4.0 tuning
        public int DiversityCapPercent { get; set; } = 60;        // max % any one subcategory may occupy in a playlist (anti-bubble; configurable)
        public double DirectorAffinityBonus { get; set; } = 0.05;  // small nudge for movies by a director the user watches
        public double SoftPenaltyStrength { get; set; } = 0.50;  // 0=hard ban during cooling, 1=no penalty; graceful sink

        // v1.4.1 tuning
        public double NewMovieBoostMinFit { get; set; } = 0.30;  // For You only boosts new movies that fit taste at/above this score
        public double NoveltyBonus { get; set; } = 0.05;          // nudge for movies not recently surfaced in playlists
        public int NoveltyHalfLifeDays { get; set; } = 30;        // days; how fast the novelty nudge fades after a movie is surfaced
        public int MinCompletionPercent { get; set; } = 50;       // v1.5.1: min playback % for a watch to count as a real signal (penalty/reward). 0 = any "played" counts (old behavior); 100 = must finish.
        public int DecayRateReferencePerWeek { get; set; } = 3;   // v1.5.3: reference watch rate. Effective affinity/novelty half-lives scale by (user's weekly rate / this). Faster watchers => quicker decay (fresher). Clamped 0.3x-3x.

        // User Exclusions
        // User GUIDs (as strings) for whom playlist generation is skipped entirely.
        public List<string> DisabledUserIds { get; set; } = new();
    }
}
