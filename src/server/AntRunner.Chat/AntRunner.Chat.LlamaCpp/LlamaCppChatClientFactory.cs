using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.LlamaCpp;

public sealed class LlamaCppChatClientFactory : IChatCompletionClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlamaCppConfig _config;
    private readonly ILogger<LlamaCppChatClient> _clientLogger;

    public LlamaCppChatClientFactory(
        IHttpClientFactory httpClientFactory,
        LlamaCppConfig config,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clientLogger = loggerFactory?.CreateLogger<LlamaCppChatClient>()
            ?? NullLogger<LlamaCppChatClient>.Instance;
    }

    public string? DefaultDeploymentId => null;

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        return CreateClientForProfile(deploymentId, (LlamaCppRuntimeProfileData?)null, false, httpClient);
    }

    public IChatCompletionClient CreateClientForProfile(
        string? deploymentId,
        LlamaCppRuntimeProfileData? profileData,
        bool parallelToolCalls,
        HttpClient? httpClient = null)
    {
        var client = httpClient ?? _httpClientFactory.CreateClient();
        if (_config.TimeoutSeconds > 0)
        {
            client.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        }

        return new LlamaCppChatClient(
            client,
            _config,
            deploymentId,
            profileData,
            parallelToolCalls,
            _clientLogger);
    }
}
