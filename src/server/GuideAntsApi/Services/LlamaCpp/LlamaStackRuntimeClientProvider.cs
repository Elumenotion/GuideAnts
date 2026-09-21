using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Resolves the runtime client for a llama-cpp model's stack (multi-stack
/// llama-cpp). Rows without a row-owned stack target use the factory-wide
/// configured client (the global LlamaCpp:BaseUrl), preserving the existing
/// behavior byte-for-byte. Row-owned stacks get a dedicated client pointing at
/// the same /llama-cpp surface on that stack (the per-stack admin/chat
/// prefixes are product decisions applied at the call site).
/// </summary>
public interface ILlamaStackRuntimeClientProvider
{
    /// <summary>
    /// The runtime client for the given row-owned stack, or null when the row
    /// targets the global stack (caller falls back to the configured client).
    /// </summary>
    ILlamaServerRuntimeClient? GetClientForStack(string? stackBaseUrl, string? stackApiKey);
}

public sealed class LlamaStackRuntimeClientProvider : ILlamaStackRuntimeClientProvider
{
    private readonly ILlamaServerRuntimeClient _globalClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, ILlamaServerRuntimeClient> _byStack = new(StringComparer.Ordinal);

    public LlamaStackRuntimeClientProvider(
        ILlamaServerRuntimeClient globalClient,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _globalClient = globalClient ?? throw new ArgumentNullException(nameof(globalClient));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public ILlamaServerRuntimeClient? GetClientForStack(string? stackBaseUrl, string? stackApiKey)
    {
        if (string.IsNullOrWhiteSpace(stackBaseUrl))
        {
            return null;
        }

        var baseUrl = stackBaseUrl.TrimEnd('/');
        // Include the key in the cache key so a rotated key yields a new client.
        var key = baseUrl + "#" + (stackApiKey ?? string.Empty);
        lock (_gate)
        {
            if (_byStack.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var client = _httpClientFactory.CreateClient("llama-stack-runtime");
            // The row stores the stack ROOT (e.g. http://192.0.2.1:8112); the llama
            // surface lives under /llama-cpp, mirroring how LlamaCpp:BaseUrl carries the
            // prefix for the global stack. Without it the client hits /models -> 404.
            client.BaseAddress = new Uri(baseUrl + "/llama-cpp");
            var logger = _loggerFactory.CreateLogger<LlamaServerRuntimeClient>();
            var instance = new LlamaServerRuntimeClient(client, logger);
            _byStack[key] = instance;
            return instance;
        }
    }

    /// <summary>
    /// The global-stack client (rows without a row-owned stack).
    /// </summary>
    public ILlamaServerRuntimeClient Global => _globalClient;
}
