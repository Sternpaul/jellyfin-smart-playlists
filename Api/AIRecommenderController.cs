using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Jellyfin.Plugin.AIRecommender.Services;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using Jellyfin.Plugin.AIRecommender.Services.Collections;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Api
{
    [ApiController]
    [Route("AIRecommender")]
    [Produces("application/json")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class AIRecommenderController : ControllerBase
    {
        private readonly AIProviderFactory _aiProviderFactory;
        private readonly LetterboxdService _letterboxdService;
        private readonly WatchHistoryService _watchHistoryService;
        private readonly PlaylistEngine _playlistEngine;
        private readonly IUserManager _userManager;
        private readonly MovieStore _movieStore;
        private readonly ITaskManager _taskManager;
        private readonly ILogger<AIRecommenderController> _logger;

        public AIRecommenderController(
            AIProviderFactory aiProviderFactory,
            LetterboxdService letterboxdService,
            WatchHistoryService watchHistoryService,
            PlaylistEngine playlistEngine,
            IUserManager userManager,
            MovieStore movieStore,
            ITaskManager taskManager,
            ILogger<AIRecommenderController> logger)
        {
            _aiProviderFactory = aiProviderFactory;
            _letterboxdService = letterboxdService;
            _watchHistoryService = watchHistoryService;
            _playlistEngine = playlistEngine;
            _userManager = userManager;
            _movieStore = movieStore;
            _taskManager = taskManager;
            _logger = logger;
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
            // Fire-and-forget REAL scheduled task (the correct, stoppable behavior).
            // Do NOT run the index/classify inside the HTTP request: that ties the work
            // to the request's CancellationToken, which cancels every in-flight call
            // (TMDB HTTP, EF save) the moment the browser closes the tab -> a forever
            // loading circle, no way to stop it, and mass TaskCanceledException spam in
            // the logs. The scheduled task runs on its own token and shows progress /
            // can be stopped from Dashboard > Scheduled Tasks.
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
            // Fire-and-forget REAL scheduled task. Same rationale as ClassifyLibrary:
            // running the refresh inside the HTTP request caused the loading-circle /
            // unstoppable / mass-cancellation regressions.
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
            await _letterboxdService.FetchRatingsFromJsonAsync(userId, cancellationToken);
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

        [HttpGet("Collections")]
        public async Task<ActionResult> GetCollections(CancellationToken cancellationToken)
        {
            var definitions = await _movieStore.GetCollectionDefinitionsAsync(cancellationToken);
            var subscriptions = await _movieStore.GetCollectionSubscriptionsAsync(cancellationToken);
            return Ok(definitions.Select(definition => new
            {
                definition.Id,
                definition.Name,
                definition.Description,
                definition.Type,
                TmdbMovieIds = ParseJsonIds<int>(definition.TmdbMovieIdsJson),
                ImdbIds = ParseJsonIds<string>(definition.ImdbIdsJson),
                AssignedUserIds = subscriptions
                    .Where(subscription => subscription.CollectionDefinitionId == definition.Id)
                    .Select(subscription => subscription.UserId)
                    .ToArray()
            }));
        }

        [HttpPost("Collections")]
        public async Task<ActionResult> SaveCollection(
            [FromBody] CollectionDefinitionRequest request,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest("Collection name is required.");
            if (request.Name.Trim().Length > 120)
                return BadRequest("Collection name may contain at most 120 characters.");
            if ((request.Description?.Length ?? 0) > 1000)
                return BadRequest("Collection description may contain at most 1000 characters.");
            if (!Enum.IsDefined(request.Type))
                return BadRequest("Collection type is invalid.");
            var tmdbMovieIds = request.TmdbMovieIds ?? new List<int>();
            var imdbIds = request.ImdbIds ?? new List<string>();
            if (tmdbMovieIds.Any(id => id <= 0))
                return BadRequest("TMDB movie IDs must be positive integers.");
            if (imdbIds.Any(string.IsNullOrWhiteSpace))
                return BadRequest("IMDb IDs cannot be blank.");
            if (imdbIds.Any(id => id.Trim().Length > 32))
                return BadRequest("IMDb IDs may contain at most 32 characters.");
            imdbIds = imdbIds
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            tmdbMovieIds = tmdbMovieIds.Distinct().ToList();
            if (tmdbMovieIds.Count == 0 && imdbIds.Count == 0)
                return BadRequest("At least one TMDB movie ID or IMDb ID is required.");
            if (tmdbMovieIds.Count + imdbIds.Count > PersistentCollectionPolicy.MaximumMembers)
                return BadRequest($"A collection may contain at most {PersistentCollectionPolicy.MaximumMembers} identifiers.");
            var existingDefinitions = await _movieStore.GetCollectionDefinitionsAsync(cancellationToken);
            if (request.Id != Guid.Empty && existingDefinitions.All(definition => definition.Id != request.Id))
                return NotFound("Collection definition not found.");
            if (existingDefinitions.Any(definition =>
                    definition.Id != request.Id &&
                    definition.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return BadRequest("Collection names must be unique.");

            var definition = new CollectionDefinition
            {
                Id = request.Id,
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                Type = request.Type,
                TmdbMovieIdsJson = JsonSerializer.Serialize(tmdbMovieIds.OrderBy(id => id)),
                ImdbIdsJson = JsonSerializer.Serialize(imdbIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            };
            await _movieStore.SaveCollectionDefinitionAsync(definition, cancellationToken);

            await RefreshAffectedCollectionUsersAsync(
                (await _movieStore.GetCollectionSubscriptionsAsync(cancellationToken))
                    .Where(subscription => subscription.CollectionDefinitionId == definition.Id)
                    .Select(subscription => subscription.UserId),
                cancellationToken);

            return Ok(new { definition.Id });
        }

        [HttpPost("Collections/Assignment")]
        public async Task<ActionResult> SetCollectionAssignment(
            [FromBody] CollectionAssignmentRequest request,
            CancellationToken cancellationToken)
        {
            if (!UserExists(request.UserId))
                return NotFound("User not found.");
            if (!(await _movieStore.GetCollectionDefinitionsAsync(cancellationToken))
                .Any(definition => definition.Id == request.CollectionDefinitionId))
                return NotFound("Collection definition not found.");

            await _movieStore.SetCollectionSubscriptionAsync(
                request.UserId,
                request.CollectionDefinitionId,
                request.Assigned,
                cancellationToken);
            await _playlistEngine.RefreshPersistentCollectionsAsync(request.UserId, cancellationToken);
            return NoContent();
        }

        [HttpDelete("Collections/{collectionDefinitionId:guid}")]
        public async Task<ActionResult> DeleteCollection(
            [FromRoute] Guid collectionDefinitionId,
            CancellationToken cancellationToken)
        {
            var affectedUsers = (await _movieStore.GetCollectionSubscriptionsAsync(cancellationToken))
                .Where(subscription => subscription.CollectionDefinitionId == collectionDefinitionId)
                .Select(subscription => subscription.UserId)
                .Distinct()
                .ToList();
            await _movieStore.DeleteCollectionDefinitionAsync(collectionDefinitionId, cancellationToken);
            await RefreshAffectedCollectionUsersAsync(affectedUsers, cancellationToken);
            return NoContent();
        }

        [HttpPost("Collections/Refresh")]
        public async Task<ActionResult> RefreshCollections(
            [FromQuery][Required] Guid userId,
            CancellationToken cancellationToken)
        {
            if (!UserExists(userId))
                return NotFound("User not found.");
            await _playlistEngine.RefreshPersistentCollectionsAsync(userId, cancellationToken);
            return NoContent();
        }

        private bool UserExists(Guid userId) =>
            _userManager.GetUsers().Any(user => user.Id == userId);

        private async Task RefreshAffectedCollectionUsersAsync(
            IEnumerable<Guid> userIds,
            CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            foreach (var userId in userIds.Distinct())
            {
                try
                {
                    await _playlistEngine.RefreshPersistentCollectionsAsync(userId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
                throw new AggregateException("One or more persistent collection reconciliations failed.", failures);
        }

        private static IReadOnlyList<T> ParseJsonIds<T>(string json)
        {
            try { return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>(); }
            catch (JsonException) { return new List<T>(); }
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

    public sealed class CollectionDefinitionRequest
    {
        public Guid Id { get; set; }
        [Required]
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public CollectionDefinitionType Type { get; set; }
        public List<int> TmdbMovieIds { get; set; } = new();
        public List<string> ImdbIds { get; set; } = new();
    }

    public sealed class CollectionAssignmentRequest
    {
        [Required]
        public Guid UserId { get; set; }
        [Required]
        public Guid CollectionDefinitionId { get; set; }
        public bool Assigned { get; set; }
    }
}
