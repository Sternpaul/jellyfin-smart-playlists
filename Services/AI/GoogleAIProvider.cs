using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services.AI
{
    public class GoogleAIProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly PluginConfiguration _config;
        private readonly ILogger<GoogleAIProvider> _logger;

        public string Name => "Google AI";

        public GoogleAIProvider(HttpClient httpClient, PluginConfiguration config, ILogger<GoogleAIProvider> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await CallApiAsync(_config.ClassificationModel, "Ping. Reply with 'Pong' in JSON.", true, cancellationToken);
                return !string.IsNullOrWhiteSpace(response);
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> ClassifyMoviesAsync(List<MovieMetadata> movies, CancellationToken cancellationToken = default)
        {
            var prompt = BuildClassificationPrompt(movies);
            return await CallApiAsync(_config.ClassificationModel, prompt, true, cancellationToken);
        }

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            return await CallApiAsync(_config.ChatModel, prompt, false, cancellationToken);
        }

        private async Task<string> CallApiAsync(string model, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
                throw new InvalidOperationException("Google AI API Key is missing.");

            var url = string.IsNullOrWhiteSpace(_config.CustomEndpoint)
                ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_config.ApiKey}"
                : $"{_config.CustomEndpoint}/models/{model}:generateContent?key={_config.ApiKey}";

            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                },
                generationConfig = forceJson ? new { responseMimeType = "application/json" } : null
            };

            var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Google AI API failed with status {Status}: {ErrorBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            
            try
            {
                return jsonResponse.GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex, "Unexpected response format from Google AI. Raw JSON: {Json}", jsonResponse.GetRawText());
                throw new Exception("Unexpected response format from Google AI.");
            }
        }

        private string BuildClassificationPrompt(List<MovieMetadata> movies)
        {
            var moviesJson = JsonSerializer.Serialize(movies.Select(m => new { m.ItemId, m.Title, m.ReleaseYear, m.Plot }));
            return $@"You are a movie classification expert. I will give you a list of movies.
Analyze the plot of each and output a JSON array of objects with the exact following schema:
[{{
  ""ItemId"": ""guid-from-input"",
  ""Subcategories"": [""Psychological Thriller"", ""Neo-Noir""],
  ""Moods"": [""dark"", ""cerebral""],
  ""Themes"": [""obsession"", ""revenge""],
  ""NarrativeStyle"": ""mystery-procedural"",
  ""Accessibility"": ""mainstream"",
  ""Intensity"": ""high"",
  ""CriticalAcclaimScore"": 8
}}]
CriticalAcclaimScore must be 1-10 based on general reputation.
Here are the movies:
{moviesJson}";
        }
    }
}
