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

    public IChatCompletionClient CreateClientForProfile(
        string? deploymentId,
        LlamaCppRuntimeProfileData? profileData,
        HttpClient? httpClient = null)
    {
        var client = httpClient ?? _httpClientFactory.CreateClient();
        // LlamaCppChatClient owns one explicit deadline token for the complete response body.
        // HttpClient.Timeout is disabled because ResponseHeadersRead otherwise stops enforcing it
        // once SSE headers arrive, and competing timeout sources cannot be classified reliably.
        client.Timeout = Timeout.InfiniteTimeSpan;

        return new LlamaCppChatClient(
            client,
            _config,
            deploymentId,
            profileData,
            _clientLogger,
            _timeoutObserver);
    }
}
