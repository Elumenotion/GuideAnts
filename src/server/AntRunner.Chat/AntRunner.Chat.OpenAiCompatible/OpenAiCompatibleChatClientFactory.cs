using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.OpenAiCompatible;

/// <summary>
/// Builds <see cref="OpenAiCompatibleChatClient"/> instances. Config is always
/// per-row (the endpoint + API key live on the catalog row), so there is no
/// factory-wide config — every call supplies a row-specific
/// <see cref="OpenAiCompatibleChatConfig"/>.
/// </summary>
public sealed class OpenAiCompatibleChatClientFactory : IChatCompletionClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiCompatibleChatClient> _logger;

    public OpenAiCompatibleChatClientFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = loggerFactory?.CreateLogger<OpenAiCompatibleChatClient>()
            ?? NullLogger<OpenAiCompatibleChatClient>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null) =>
        CreateClientForBehavior(deploymentId, behavior: null, httpClient, rowConfig: null);

    /// <summary>
    /// Creates a client bound to the catalog row's connection details
    /// (<paramref name="rowConfig"/>) and model-owned chat behavior
    /// (thinking control and extra request fields).
    /// </summary>
    public IChatCompletionClient CreateClientForBehavior(
        string? deploymentId,
        ProviderChatBehavior? behavior,
        HttpClient? httpClient,
        OpenAiCompatibleChatConfig? rowConfig)
    {
        if (rowConfig == null)
        {
            throw new InvalidOperationException(
                "OpenAI-compatible chat requires a per-row configuration (baseUrl).");
        }

        return new OpenAiCompatibleChatClient(
            httpClient ?? _httpClientFactory.CreateClient(),
            rowConfig,
            deploymentId,
            _logger,
            behavior);
    }
}
