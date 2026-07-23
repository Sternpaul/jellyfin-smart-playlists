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
        private readonly ILogger<GoogleAIProvider> _logger;

        public string Name => "Google AI";

        public GoogleAIProvider(HttpClient httpClient, ILogger<GoogleAIProvider> logger)
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
                throw new InvalidOperationException("Google AI API Key is missing.");

            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Config.ApiKey}"
                : $"{Config.CustomEndpoint}/models/{model}:generateContent?key={Config.ApiKey}";

            object generationConfig;
            if (forceJson)
            {
                generationConfig = new 
                { 
                    temperature = 0.2, 
                    responseMimeType = "application/json",
                    responseSchema = new
                    {
                        type = "ARRAY",
                        items = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                ItemId = new { type = "STRING" },
                                Subcategories = new { type = "ARRAY", items = new { type = "STRING" } },
                                Moods = new { type = "ARRAY", items = new { type = "STRING" } },
                                Themes = new { type = "ARRAY", items = new { type = "STRING" } },
                                NarrativeStyle = new { type = "STRING" },
                                Accessibility = new { type = "STRING" },
                                Intensity = new { type = "STRING" },
                                CriticalAcclaimScore = new { type = "INTEGER" }
                            }
                        }
                    }
                };
            }
            else
            {
                generationConfig = new { temperature = 0.2 };
            }

            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                },
                generationConfig = generationConfig
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
