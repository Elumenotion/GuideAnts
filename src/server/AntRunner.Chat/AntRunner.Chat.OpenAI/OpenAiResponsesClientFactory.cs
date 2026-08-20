using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.OpenAI;

public sealed class OpenAiResponsesClientFactory : IChatCompletionClientFactory
{
    private readonly AzureOpenAiConfig? _config;
    private readonly Func<AzureOpenAiConfig>? _configAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiResponsesClient> _clientLogger;

    public OpenAiResponsesClientFactory(
        IHttpClientFactory httpClientFactory,
        AzureOpenAiConfig? config = null,
        Func<AzureOpenAiConfig>? configAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config;
        _configAccessor = configAccessor;
        _clientLogger = loggerFactory?.CreateLogger<OpenAiResponsesClient>()
            ?? NullLogger<OpenAiResponsesClient>.Instance;
    }

    public string? DefaultDeploymentId => GetCurrentConfig().DeploymentId;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var config = GetCurrentConfig();
        var effectiveConfig = config with
        {
            DeploymentId = deploymentId ?? config.DeploymentId
        };
        var effectiveHttpClient = httpClient ?? _httpClientFactory.CreateClient();
        return new OpenAiResponsesClient(effectiveHttpClient, effectiveConfig, _clientLogger);
    }

    private AzureOpenAiConfig GetCurrentConfig()
    {
        return _configAccessor?.Invoke()
            ?? _config
            ?? throw new InvalidOperationException("OpenAI configuration is required.");
    }
}
