using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.GoogleGemini;

public sealed class GoogleGeminiChatClientFactory : IChatCompletionClientFactory
{
    private readonly GoogleGeminiChatConfig? _config;
    private readonly Func<GoogleGeminiChatConfig>? _configAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleGeminiChatClient> _logger;

    public GoogleGeminiChatClientFactory(
        IHttpClientFactory httpClientFactory,
        GoogleGeminiChatConfig? config = null,
        Func<GoogleGeminiChatConfig>? configAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config;
        _configAccessor = configAccessor;
        _logger = loggerFactory?.CreateLogger<GoogleGeminiChatClient>()
            ?? NullLogger<GoogleGeminiChatClient>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        var resolvedConfig = _configAccessor?.Invoke()
            ?? _config
            ?? throw new InvalidOperationException("Google Gemini chat configuration is not available.");
        return new GoogleGeminiChatClient(
            httpClient ?? _httpClientFactory.CreateClient(),
            resolvedConfig,
            deploymentId,
            _logger);
    }
}
