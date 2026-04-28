using System.Collections.Concurrent;
using AntRunner.Chat.Abstractions;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.Anthropic;

public sealed class AnthropicChatClientFactory : IChatCompletionClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, AnthropicClient> _clientCache = new();
    private readonly AnthropicConfig? _config;
    private readonly Func<AnthropicConfig>? _configAccessor;
    private readonly ILogger<AnthropicChatClient> _clientLogger;
    private string? _activeSignature;

    public AnthropicChatClientFactory(
        IHttpClientFactory httpClientFactory,
        AnthropicConfig? config = null,
        Func<AnthropicConfig>? configAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config;
        _configAccessor = configAccessor;
        _clientLogger = loggerFactory?.CreateLogger<AnthropicChatClient>()
            ?? NullLogger<AnthropicChatClient>.Instance;
    }

    public string? DefaultDeploymentId => Config.DefaultModel;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var config = Config;
        var client = GetOrCreateClient(httpClient);
        return new AnthropicChatClient(
            client,
            config.DefaultModel,
            config.DefaultMaxTokens,
            config.ThinkingBudgets,
            _clientLogger);
    }

    private AnthropicClient GetOrCreateClient(HttpClient? overrideClient)
    {
        var config = Config;
        EnsureCacheSignature(config);

        if (overrideClient != null)
        {
            return CreateClientInstance(config, overrideClient);
        }

        var key = BuildSignature(config);
        return _clientCache.GetOrAdd(key, _ =>
        {
            var httpClient = _httpClientFactory.CreateClient();
            return CreateClientInstance(config, httpClient);
        });
    }

    private AnthropicClient CreateClientInstance(AnthropicConfig config, HttpClient httpClient)
    {
        var options = new ClientOptions
        {
            ApiKey = config.ApiKey,
            AuthToken = config.AuthToken,
            HttpClient = httpClient
        };

        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            options.BaseUrl = config.BaseUrl!;
        }

        return new AnthropicClient(options);
    }

    private AnthropicConfig Config => _configAccessor?.Invoke()
        ?? _config
        ?? throw new InvalidOperationException("Anthropic configuration is required.");

    private void EnsureCacheSignature(AnthropicConfig config)
    {
        var signature = BuildSignature(config);
        if (string.Equals(signature, _activeSignature, StringComparison.Ordinal))
        {
            return;
        }

        _clientCache.Clear();
        _activeSignature = signature;
    }

    private static string BuildSignature(AnthropicConfig config)
    {
        return $"{config.ApiKey}:{config.AuthToken}:{config.BaseUrl}:{config.DefaultModel}:{config.DefaultMaxTokens}";
    }
}
