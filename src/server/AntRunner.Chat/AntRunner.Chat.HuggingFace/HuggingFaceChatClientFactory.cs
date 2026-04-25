using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.HuggingFace;

public sealed class HuggingFaceChatClientFactory : IChatCompletionClientFactory
{
    private readonly HuggingFaceChatConfig? _config;
    private readonly Func<HuggingFaceChatConfig>? _configAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HuggingFaceChatClient> _logger;

    public HuggingFaceChatClientFactory(
        IHttpClientFactory httpClientFactory,
        HuggingFaceChatConfig? config = null,
        Func<HuggingFaceChatConfig>? configAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config;
        _configAccessor = configAccessor;
        _logger = loggerFactory?.CreateLogger<HuggingFaceChatClient>()
            ?? NullLogger<HuggingFaceChatClient>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var resolvedConfig = _configAccessor?.Invoke()
            ?? _config
            ?? throw new InvalidOperationException("Hugging Face chat configuration is not available.");
        return new HuggingFaceChatClient(
            httpClient ?? _httpClientFactory.CreateClient(),
            resolvedConfig,
            deploymentId,
            _logger);
    }
}
