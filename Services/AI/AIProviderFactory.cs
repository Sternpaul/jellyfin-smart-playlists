using System;
using System.Net.Http;
using Jellyfin.Plugin.AIRecommender.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services.AI
{
    public class AIProviderFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly PluginConfiguration _config;
        private readonly ILoggerFactory _loggerFactory;

        public AIProviderFactory(IHttpClientFactory httpClientFactory, PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _loggerFactory = loggerFactory;
        }

        public IAIProvider GetProvider()
        {
            var httpClient = _httpClientFactory.CreateClient();

            return _config.AIProvider switch
            {
                AIProviderType.GoogleAI => new GoogleAIProvider(httpClient, _config, _loggerFactory.CreateLogger<GoogleAIProvider>()),
                AIProviderType.OpenRouter => new OpenRouterProvider(httpClient, _config, _loggerFactory.CreateLogger<OpenRouterProvider>()),
                AIProviderType.OpenAI => new OpenAIProvider(httpClient, _config, _loggerFactory.CreateLogger<OpenAIProvider>()),
                AIProviderType.Anthropic => new AnthropicProvider(httpClient, _config, _loggerFactory.CreateLogger<AnthropicProvider>()),
                _ => throw new NotImplementedException($"AI Provider {_config.AIProvider} is not implemented.")
            };
        }
    }
}
