using System.Collections.Concurrent;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace AntRunner.Chat.OpenAI;

public sealed class OpenAiChatClientFactory : IChatCompletionClientFactory
{
    private readonly AzureOpenAiConfig? _config;
    private readonly Func<AzureOpenAiConfig>? _configAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiChatClient> _clientLogger;
    private readonly ConcurrentDictionary<string, OpenAIClient> _clientCache = new();
    private string? _activeSignature;

    public OpenAiChatClientFactory(
        IHttpClientFactory httpClientFactory,
        AzureOpenAiConfig? config = null,
        Func<AzureOpenAiConfig>? configAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config;
        _configAccessor = configAccessor;
        _clientLogger = loggerFactory?.CreateLogger<OpenAiChatClient>()
            ?? NullLogger<OpenAiChatClient>.Instance;
    }

    public string? DefaultDeploymentId => GetCurrentConfig().DeploymentId;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var client = GetOrCreateOpenAiClient(deploymentId, httpClient);
        return new OpenAiChatClient(client, _clientLogger);
    }

    private OpenAIClient GetOrCreateOpenAiClient(string? deploymentId, HttpClient? overrideClient)
    {
        var config = GetCurrentConfig();
        EnsureCacheSignature(config);

        if (overrideClient != null)
        {
            return CreateClientInstance(config, deploymentId, overrideClient);
        }

        var key = $"{BuildSignature(config)}:{deploymentId ?? string.Empty}";
        return _clientCache.GetOrAdd(key, _ =>
        {
            var client = _httpClientFactory.CreateClient();
            return CreateClientInstance(config, deploymentId, client);
        });
    }

    private OpenAIClient CreateClientInstance(AzureOpenAiConfig config, string? deploymentId, HttpClient httpClient)
    {
        var auth = new OpenAIAuthentication(config.ApiKey);
        var settings = OpenAiClientSettingsFactory.Create(config, deploymentId);
        return new OpenAIClient(auth, settings, httpClient);
    }

    private AzureOpenAiConfig GetCurrentConfig()
    {
        return _configAccessor?.Invoke()
            ?? _config
            ?? AzureOpenAiConfigFactory.Get()
            ?? new AzureOpenAiConfig();
    }

    private void EnsureCacheSignature(AzureOpenAiConfig config)
    {
        var signature = BuildSignature(config);
        if (string.Equals(signature, _activeSignature, StringComparison.Ordinal))
        {
            return;
        }

        _clientCache.Clear();
        _activeSignature = signature;
    }

    private static string BuildSignature(AzureOpenAiConfig config)
    {
        return $"{config.ApiKey}:{config.ResourceName}:{config.ApiVersion}:{config.DeploymentId}";
    }
}
