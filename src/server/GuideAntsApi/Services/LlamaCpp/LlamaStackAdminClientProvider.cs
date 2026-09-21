using GuideAntsApi.Services.Bootstrap;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Resolves the llama admin (router) client for a catalog model's stack. The
/// model determines the machine that runs it: rows with a row-owned stack
/// (RuntimeConfigJson.stackBaseUrl) have their router INI read/written on
/// THAT stack's own admin surface; rows without one target the factory-wide
/// configured client (global LlamaCpp:BaseUrl), preserving existing behavior
/// byte-for-byte. Stacks do not know about each other; this provider only
/// ever talks to the single stack a row points at.
/// </summary>
public interface ILlamaStackAdminClientProvider
{
    /// <summary>
    /// The admin client for the given row-owned stack, or null when the row
    /// targets the global stack (caller falls back to the configured client).
    /// </summary>
    ILlamaRuntimeAdminClient? GetClientForStack(string? stackBaseUrl, string? stackApiKey);
}

public sealed class LlamaStackAdminClientProvider : ILlamaStackAdminClientProvider
{
    private readonly ILlamaRuntimeAdminClient _globalClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, ILlamaRuntimeAdminClient> _byStack = new(StringComparer.Ordinal);

    public LlamaStackAdminClientProvider(
        ILlamaRuntimeAdminClient globalClient,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _globalClient = globalClient ?? throw new ArgumentNullException(nameof(globalClient));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public ILlamaRuntimeAdminClient? GetClientForStack(string? stackBaseUrl, string? stackApiKey)
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

            var client = _httpClientFactory.CreateClient("llama-stack-admin");
            // The row stores the stack ROOT (e.g. http://192.0.2.1:8112); derive the
            // same admin surface the global client binds ({root}/llama-admin/), mirroring
            // ApplyLlamaRuntimeAdminBaseAddress. Loopback port 9 is the "not in use"
            // sentinel; Apply* leaves BaseAddress unset for it.
            LocalAiStackHostUrls.ApplyLlamaRuntimeAdminBaseAddress(client, baseUrl);
            client.Timeout = TimeSpan.FromHours(4);
            if (!string.IsNullOrEmpty(stackApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", stackApiKey);
            }

            var logger = _loggerFactory.CreateLogger<LlamaRuntimeAdminClient>();
            var instance = new LlamaRuntimeAdminClient(client, logger);
            _byStack[key] = instance;
            return instance;
        }
    }

    /// <summary>
    /// The global-stack admin client (rows without a row-owned stack).
    /// </summary>
    public ILlamaRuntimeAdminClient Global => _globalClient;
}
