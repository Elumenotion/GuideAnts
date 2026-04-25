using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.OpenRouter;

public sealed class OpenRouterChatClientFactory : IChatCompletionClientFactory
{
    private readonly OpenRouterChatConfig? _config;
    private readonly Func<OpenRouterChatConfig>? _configAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenRouterChatClient> _logger;

    public OpenRouterChatClientFactory(
        IHttpClientFactory httpClientFactory,
        OpenRouterChatConfig? config = null,
        Func<OpenRouterChatConfig>? configAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config;
        _configAccessor = configAccessor;
        _logger = loggerFactory?.CreateLogger<OpenRouterChatClient>()
            ?? NullLogger<OpenRouterChatClient>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var resolvedConfig = _configAccessor?.Invoke()
            ?? _config
            ?? throw new InvalidOperationException("OpenRouter chat configuration is not available.");
        return new OpenRouterChatClient(
            httpClient ?? _httpClientFactory.CreateClient(),
            resolvedConfig,
            deploymentId,
            _logger);
    }
}
