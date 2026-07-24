using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Jellyfin.Plugin.AIRecommender.Services;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AIRecommender.Api
{
    [ApiController]
    [Route("AIRecommender")]
    [Produces("application/json")]
    [Authorize]
    public class AIRecommenderController : ControllerBase
    {
        private readonly AIProviderFactory _aiProviderFactory;
        private readonly LetterboxdService _letterboxdService;
        private readonly WatchHistoryService _watchHistoryService;
        private readonly PlaylistEngine _playlistEngine;
        private readonly IUserManager _userManager;
        private readonly MovieStore _movieStore;
        private readonly ITaskManager _taskManager;

        public AIRecommenderController(
            AIProviderFactory aiProviderFactory,
            LetterboxdService letterboxdService,
            WatchHistoryService watchHistoryService,
            PlaylistEngine playlistEngine,
            IUserManager userManager,
            MovieStore movieStore,
            ITaskManager taskManager)
        {
            _aiProviderFactory = aiProviderFactory;
            _letterboxdService = letterboxdService;
            _watchHistoryService = watchHistoryService;
            _playlistEngine = playlistEngine;
            _userManager = userManager;
            _movieStore = movieStore;
            _taskManager = taskManager;
        }

        [HttpPost("Chat")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken)
        {
            var provider = _aiProviderFactory.GetProvider();
            
            var user = _userManager.GetUserById(request.UserId);
            if (user == null) return NotFound("User not found.");

            // Fetch taste profile context to inject into prompt
            var profile = await _watchHistoryService.GetUserTasteProfileAsync(request.UserId, cancellationToken);
            
            string systemPrompt = "You are an AI movie recommendation assistant integrated into Jellyfin. " +
                                  "You help users pick movies. The user has preferences for: " +
                                  string.Join(", ", profile.SubcategoryPreferences.Keys);

            var reply = await provider.ChatAsync(request.Message, systemPrompt, cancellationToken);
            
            return Ok(new ChatResponse { Reply = reply });
        }

        [HttpGet("TestConnection")]
        public async Task<ActionResult> TestConnection([FromQuery] string provider, [FromQuery] string apiKey, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                    return Ok(new { Success = false, Message = "Please enter an API Key first." });

                if (!Enum.TryParse<AIProviderType>(provider, out var providerType))
                    return Ok(new { Success = false, Message = $"Unknown provider: {provider}" });

                // The providers read the key/model from Plugin.Instance.Configuration.
                // The user may not have saved yet, so temporarily override with what they
                // just typed, then restore in finally. (Test Connection runs rarely; this
                // in-memory swap is safe against the rest of the app.)
                var config = Plugin.Instance!.Configuration;
                var originalProvider = config.AIProvider;
                var originalKey = config.ApiKey;

                config.AIProvider = providerType;
                config.ApiKey = apiKey;

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(90));

                    var aiProvider = _aiProviderFactory.GetProvider();
                    var success = await aiProvider.ValidateConnectionAsync(cts.Token);

                    return Ok(new
                    {
                        Success = success,
                        Message = success
                            ? "Connection Successful!"
                            : "Connection failed. Check the API key and that the model name is valid for this provider."
                    });
                }
                finally
                {
                    config.AIProvider = originalProvider;
                    config.ApiKey = originalKey;
                }
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, Message = ex.Message });
            }
        }
        
        [HttpPost("ClassifyLibrary")]
        public ActionResult ClassifyLibrary()
        {
            var task = _taskManager.ScheduledTasks.FirstOrDefault(t => t.Name == "AI Recommender - Index & Classify Library");
            if (task != null)
            {
                _taskManager.Execute(task, new MediaBrowser.Model.Tasks.TaskOptions());
                return NoContent();
            }
            return NotFound("Task not found");
        }
        
        [HttpPost("RefreshPlaylists")]
        public ActionResult RefreshAllPlaylists()
        {
            var task = _taskManager.ScheduledTasks.FirstOrDefault(t => t.Name == "AI Recommender - Refresh Playlists");
            if (task != null)
            {
                _taskManager.Execute(task, new MediaBrowser.Model.Tasks.TaskOptions());
                return NoContent();
            }
            return NotFound("Task not found");
        }
        
        [HttpGet("UserWatchlistConfig")]
        public async Task<ActionResult<UserWatchlistConfig>> GetUserWatchlistConfig([FromQuery][Required] Guid userId, CancellationToken cancellationToken)
        {
            var config = await _movieStore.GetUserWatchlistConfigAsync(userId, cancellationToken);
            if (config == null) return Ok(new UserWatchlistConfig { UserId = userId });
            return Ok(config);
        }
        
        [HttpPost("UserWatchlistConfig")]
        public async Task<ActionResult> SaveUserWatchlistConfig([FromBody] UserWatchlistConfig request, CancellationToken cancellationToken)
        {
            // Derive the import method from the provided data so the config page doesn't
            // need to send it explicitly, and so configs saved before this fix (which left
            // ImportMethod = None) start syncing on the next refresh.
            if (request.ImportMethod == WatchlistImportMethod.None)
            {
                if (!string.IsNullOrWhiteSpace(request.JsonUrl))
                    request.ImportMethod = WatchlistImportMethod.JsonUrl;
                else if (!string.IsNullOrWhiteSpace(request.CsvData))
                    request.ImportMethod = WatchlistImportMethod.CsvUpload;
            }
            await _movieStore.SaveUserWatchlistConfigAsync(request, cancellationToken);
            return NoContent();
        }

        [HttpPost("UserConfig/SyncLetterboxd")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> SyncLetterboxd([FromQuery][Required] Guid userId, CancellationToken cancellationToken)
        {
            await _letterboxdService.SyncWatchlistAsync(userId, cancellationToken);
            return NoContent();
        }

        [HttpPost("UserConfig/SyncRatings")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> SyncRatings([FromQuery][Required] Guid userId, CancellationToken cancellationToken)
        {
            await _letterboxdService.ScrapeRatingsAsync(userId, cancellationToken);
            return NoContent();
        }

        [HttpPost("Playlists/Refresh")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> RefreshUserPlaylists([FromQuery][Required] Guid userId, CancellationToken cancellationToken)
        {
            await _playlistEngine.RefreshUserPlaylistsAsync(userId, cancellationToken);
            return NoContent();
        }

        // v1.5.9: immediately purge playlists for users disabled in config, so exclusions
        // take effect on save instead of waiting for the next scheduled refresh.
        [HttpPost("ApplyExclusions")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> ApplyExclusions(CancellationToken cancellationToken)
        {
            var disabled = (Plugin.Instance?.Configuration?.DisabledUserIds ?? new List<string>())
                .Where(id => Guid.TryParse(id, out _))
                .Select(id => Guid.Parse(id))
                .ToHashSet();
            if (disabled.Count == 0) return NoContent();
            foreach (var user in _userManager.GetUsers())
            {
                var idProp = user.GetType().GetProperty("Id");
                if (idProp == null) continue;
                if (idProp.GetValue(user) is not Guid userId) continue;
                if (disabled.Contains(userId))
                    await _playlistEngine.ApplyExclusionsNowAsync(new[] { userId }, cancellationToken);
            }
            return NoContent();
        }

        // v1.5.0: read-only "what's happening" snapshot for the config-page debug panel.
        [HttpGet("Debug/{userId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> GetDebugSnapshot([FromRoute][Required] Guid userId, CancellationToken cancellationToken)
        {
            var snapshot = await _playlistEngine.GetDebugSnapshotAsync(userId, cancellationToken);
            return Ok(snapshot);
        }
    }

    public class ChatRequest
    {
        [Required]
        public Guid UserId { get; set; }
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    public class ChatResponse
    {
        public string Reply { get; set; } = string.Empty;
    }
}
