using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services.AI
{
    public class AnthropicProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly PluginConfiguration _config;
        private readonly ILogger<AnthropicProvider> _logger;

        public string Name => "Anthropic Claude";

        public AnthropicProvider(HttpClient httpClient, PluginConfiguration config, ILogger<AnthropicProvider> logger)
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
                throw new InvalidOperationException("Anthropic API Key is missing.");

            var url = string.IsNullOrWhiteSpace(_config.CustomEndpoint)
                ? "https://api.anthropic.com/v1/messages"
                : _config.CustomEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-api-key", _config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            if (forceJson)
            {
                prompt += "\n\nRespond ONLY with valid JSON. Do not include markdown formatting or explanations.";
            }

            var requestBody = new
            {
                model = model,
                max_tokens = 4096,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var jsonBody = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Anthropic API failed with status {Status}: {ErrorBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            
            try
            {
                return jsonResponse.GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex, "Unexpected response format from Anthropic. Raw JSON: {Json}", jsonResponse.GetRawText());
                throw new Exception("Unexpected response format from Anthropic.");
            }
        }

        private string BuildClassificationPrompt(List<MovieMetadata> movies)
        {
            var moviesJson = JsonSerializer.Serialize(movies.Select(m => new { m.ItemId, m.Title, m.ReleaseYear, m.Plot }));
            return $@"You are a movie classification expert. I will give you a list of movies.
Analyze the plot of each and output a JSON object containing a ""movies"" array with the exact following schema:
{{
  ""movies"": [
    {{
      ""ItemId"": ""guid-from-input"",
      ""Subcategories"": [""Psychological Thriller"", ""Neo-Noir""],
      ""Moods"": [""dark"", ""cerebral""],
      ""Themes"": [""obsession"", ""revenge""],
      ""NarrativeStyle"": ""mystery-procedural"",
      ""Accessibility"": ""mainstream"",
      ""Intensity"": ""high"",
      ""CriticalAcclaimScore"": 8
    }}
  ]
}}
CriticalAcclaimScore must be 1-10 based on general reputation.
Here are the movies:
{moviesJson}";
        }
    }
}
