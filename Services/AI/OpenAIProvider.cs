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
        private readonly ILogger<OpenAIProvider> _logger;

        public string Name => "OpenAI";

        public OpenAIProvider(HttpClient httpClient, ILogger<OpenAIProvider> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private PluginConfiguration Config => Plugin.Instance!.Configuration;

        public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await CallApiAsync(Config.ClassificationModel, "Ping. Reply with 'Pong' in JSON.", true, cancellationToken);
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
            return await CallApiAsync(Config.ClassificationModel, prompt, true, cancellationToken);
        }

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            return await CallApiAsync(Config.ChatModel, prompt, false, cancellationToken);
        }

        private async Task<string> CallApiAsync(string model, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Config.ApiKey))
                throw new InvalidOperationException("OpenAI API Key is missing.");

            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? "https://api.openai.com/v1/chat/completions"
                : Config.CustomEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

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
            return $@"Convert the following movies into a JSON array.

Example Input:
[{{ ""ItemId"": ""00000000-0000-0000-0000-000000000000"", ""Title"": ""The Matrix"", ""ReleaseYear"": 1999, ""Plot"": ""A hacker learns the truth."" }}]

Example Output:
[
  {{
    ""ItemId"": ""00000000-0000-0000-0000-000000000000"",
    ""Subcategories"": [""Sci-Fi"", ""Action""],
    ""Moods"": [""cerebral"", ""tense""],
    ""Themes"": [""simulation"", ""rebellion""],
    ""NarrativeStyle"": ""hero-journey"",
    ""Accessibility"": ""mainstream"",
    ""Intensity"": ""high"",
    ""CriticalAcclaimScore"": 9
  }}
]

Now do it for the following movies. Output ONLY the JSON array. Do not include markdown formatting.
Input:
{moviesJson}

Output:";
        }
    }
}
