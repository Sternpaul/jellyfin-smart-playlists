using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Jellyfin.Plugin.AIRecommender.Services;
using Jellyfin.Plugin.AIRecommender.Services.AI;
using MediaBrowser.Controller.Library;
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

        public AIRecommenderController(
            AIProviderFactory aiProviderFactory,
            LetterboxdService letterboxdService,
            WatchHistoryService watchHistoryService,
            PlaylistEngine playlistEngine,
            IUserManager userManager)
        {
            _aiProviderFactory = aiProviderFactory;
            _letterboxdService = letterboxdService;
            _watchHistoryService = watchHistoryService;
            _playlistEngine = playlistEngine;
            _userManager = userManager;
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

        [HttpPost("UserConfig/SyncLetterboxd")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> SyncLetterboxd([FromQuery][Required] Guid userId, CancellationToken cancellationToken)
        {
            await _letterboxdService.SyncWatchlistAsync(userId, cancellationToken);
            return NoContent();
        }

        [HttpPost("Playlists/Refresh")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> RefreshPlaylists([FromQuery][Required] Guid userId, CancellationToken cancellationToken)
        {
            await _playlistEngine.RefreshUserPlaylistsAsync(userId, cancellationToken);
            return NoContent();
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
