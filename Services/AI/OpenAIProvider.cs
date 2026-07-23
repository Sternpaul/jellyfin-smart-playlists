using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
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
    public class OpenAIProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly PluginConfiguration _config;
        private readonly ILogger<OpenAIProvider> _logger;

        public string Name => "OpenAI";

        public OpenAIProvider(HttpClient httpClient, PluginConfiguration config, ILogger<OpenAIProvider> logger)
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
                throw new InvalidOperationException("OpenAI API Key is missing.");

            var url = string.IsNullOrWhiteSpace(_config.CustomEndpoint)
                ? "https://api.openai.com/v1/chat/completions"
                : _config.CustomEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                response_format = forceJson ? new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "movie_classification",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                movies = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            ItemId = new { type = "string" },
                                            Subcategories = new { type = "array", items = new { type = "string" } },
                                            Moods = new { type = "array", items = new { type = "string" } },
                                            Themes = new { type = "array", items = new { type = "string" } },
                                            NarrativeStyle = new { type = "string" },
                                            Accessibility = new { type = "string" },
                                            Intensity = new { type = "string" },
                                            CriticalAcclaimScore = new { type = "integer" }
                                        },
                                        required = new[] { "ItemId", "Subcategories", "Moods", "Themes", "NarrativeStyle", "Accessibility", "Intensity", "CriticalAcclaimScore" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "movies" },
                            additionalProperties = false
                        }
                    }
                } : null
            };

            var jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OpenAI API failed with status {Status}: {ErrorBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            
            try
            {
                return jsonResponse.GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex, "Unexpected response format from OpenAI. Raw JSON: {Json}", jsonResponse.GetRawText());
                throw new Exception("Unexpected response format from OpenAI.");
            }
        }

        private string BuildClassificationPrompt(List<MovieMetadata> movies)
        {
            var moviesJson = JsonSerializer.Serialize(movies.Select(m => new { m.ItemId, m.Title, m.ReleaseYear, m.Plot }));
            return $@"You are a movie classification expert. I will give you a list of movies.
Analyze the plot of each and output data according to the json schema.
CriticalAcclaimScore must be 1-10 based on general reputation.
Here are the movies:
{moviesJson}";
        }
    }
}
