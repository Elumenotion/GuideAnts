using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.LlamaCpp;

public sealed class LlamaCppChatClientFactory : IChatCompletionClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlamaCppConfig _config;
    private readonly ILogger<LlamaCppChatClient> _clientLogger;
    private readonly ILlamaInferenceTimeoutObserver _timeoutObserver;

    public LlamaCppChatClientFactory(
        IHttpClientFactory httpClientFactory,
        LlamaCppConfig config,
        ILoggerFactory? loggerFactory = null,
        ILlamaInferenceTimeoutObserver? timeoutObserver = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clientLogger = loggerFactory?.CreateLogger<LlamaCppChatClient>()
            ?? NullLogger<LlamaCppChatClient>.Instance;
        _timeoutObserver = timeoutObserver ?? NullLlamaInferenceTimeoutObserver.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        return CreateClientForProfile(deploymentId, (LlamaCppRuntimeProfileData?)null, httpClient);
    }

    /// <summary>
    /// Creates a client with an explicit per-call <paramref name="configOverride"/>
    /// (e.g. a row-owned llama stack BaseUrl/ApiKey for multi-stack llama-cpp models).
    /// When null, the factory-wide configured profile is used, as before.
    /// </summary>
    public IChatCompletionClient CreateClientForProfile(
        string? deploymentId,
        LlamaCppRuntimeProfileData? profileData,
        LlamaCppConfig? configOverride,
        HttpClient? httpClient = null)
    {
        var config = configOverride ?? _config;
        var client = httpClient ?? _httpClientFactory.CreateClient();
        // LlamaCppChatClient owns one explicit deadline token for the complete response body.
        // HttpClient.Timeout is disabled because ResponseHeadersRead otherwise stops enforcing it
        // once SSE headers arrive, and competing timeout sources cannot be classified reliably.
        client.Timeout = Timeout.InfiniteTimeSpan;

        return new LlamaCppChatClient(
            client,
            config,
            deploymentId,
            profileData,
            _clientLogger,
            _timeoutObserver);
    }

    public IChatCompletionClient CreateClientForProfile(
        string? deploymentId,
        LlamaCppRuntimeProfileData? profileData,
        HttpClient? httpClient = null)
    {
        return CreateClientForProfile(deploymentId, profileData, configOverride: null, httpClient);
    }
}
