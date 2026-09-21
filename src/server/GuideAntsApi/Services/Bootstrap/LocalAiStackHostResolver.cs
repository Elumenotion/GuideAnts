using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalAiStackHostResolver
{
    bool HasAnyConfiguredStack();

    IReadOnlyList<string> GetAllConfiguredStackBases();

    /// <summary>
    /// Config-declared stack bases plus row-owned llama-cpp instances (the distinct
    /// <c>stackBaseUrl</c> values on active llama-cpp model rows). Row-owned instances are
    /// independent stacks: the plan must be applied to them, so they are part of the
    /// warmup universe. DB failures degrade to the config-only set.
    /// </summary>
    Task<IReadOnlyList<string>> GetAllConfiguredStackBasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The configured instance universe, deduplicated by canonical identity (host resolved
    /// to IP:port), so one physical box configured under several host names is one instance.
    /// </summary>
    Task<IReadOnlyList<LocalAiInstance>> GetAllConfiguredInstancesAsync(CancellationToken cancellationToken = default);

    string? GetStackBaseForService(string serviceId);
}

public sealed class LocalAiStackHostResolver : ILocalAiStackHostResolver
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<LocalAiStackHostResolver>? _logger;

    public LocalAiStackHostResolver(IConfiguration configuration)
        : this(configuration, scopeFactory: null)
    {
    }

    public LocalAiStackHostResolver(
        IConfiguration configuration,
        IServiceScopeFactory? scopeFactory,
        ILogger<LocalAiStackHostResolver>? logger = null)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool HasAnyConfiguredStack() => GetAllConfiguredStackBases().Count > 0;

    public IReadOnlyList<string> GetAllConfiguredStackBases()
    {
        var stacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serviceId in LocalAiStackHostUrls.WarmupServiceIds)
        {
            var stack = GetStackBaseForService(serviceId);
            if (stack is not null)
            {
                stacks.Add(stack);
            }
        }

        return stacks.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<IReadOnlyList<string>> GetAllConfiguredStackBasesAsync(CancellationToken cancellationToken = default)
    {
        if (_scopeFactory is null)
        {
            return Task.FromResult(GetAllConfiguredStackBases());
        }

        return GetAllConfiguredStackBasesCoreAsync(cancellationToken);
    }

    /// <summary>
    /// The configured instance universe, deduplicated by <see cref="CanonicalInstanceKey"/>.
    /// One physical box must map to exactly one instance even when it is configured under
    /// multiple host names (e.g. an aux host <c>max:8112</c> and a llama row
    /// <c>192.0.2.1:8112</c> are the same GPU box). The first base seen for a
    /// canonical key is the transport base.
    /// </summary>
    public async Task<IReadOnlyList<LocalAiInstance>> GetAllConfiguredInstancesAsync(CancellationToken cancellationToken = default)
    {
        var byCanonical = new Dictionary<string, LocalAiInstance>(StringComparer.OrdinalIgnoreCase);
        void Add(string? base_)
        {
            if (string.IsNullOrWhiteSpace(base_)) return;
            var canonical = CanonicalInstanceKey(base_);
            if (!byCanonical.ContainsKey(canonical))
            {
                byCanonical[canonical] = new LocalAiInstance(canonical, base_);
            }
        }

        foreach (var base_ in GetAllConfiguredStackBases())
        {
            Add(base_);
        }

        if (_scopeFactory is not null)
        {
            foreach (var base_ in await GetRowOwnedLlamaStackBasesAsync(cancellationToken).ConfigureAwait(false))
            {
                Add(base_);
            }
        }

        return byCanonical.Values.OrderBy(i => i.CanonicalKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> CanonicalKeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Instance identity for lifecycle plans: the host resolved to a literal IP address
    /// (first address, IPv4 preferred) plus the port, so two host names that resolve to
    /// the same box produce the same key. Unresolvable hosts fall back to their own
    /// host:port form.
    /// </summary>
    public static string CanonicalInstanceKey(string stackBaseUrl)
    {
        if (CanonicalKeyCache.TryGetValue(stackBaseUrl, out var cached))
        {
            return cached;
        }

        var host = string.Empty;
        var port = 0;
        try
        {
            var uri = new Uri(stackBaseUrl);
            host = uri.DnsSafeHost;
            port = uri.Port;
        }
        catch (UriFormatException)
        {
            return CanonicalKeyCache.AddOrUpdate(stackBaseUrl, value => value, (_, v) => v);
        }

        string resolved;
        try
        {
            if (System.Net.IPAddress.TryParse(host, out _))
            {
                resolved = host;
            }
            else
            {
                var addresses = System.Net.Dns.GetHostAddresses(host);
                var preferred = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault();
                resolved = preferred?.ToString() ?? host;
            }
        }
        catch (System.Net.Sockets.SocketException)
        {
            resolved = host;
        }

        var canonical = port == 80 || port == 443 ? resolved : resolved + ":" + port;
        return CanonicalKeyCache.AddOrUpdate(stackBaseUrl, canonical, static (_, v) => v);
    }

    private async Task<IReadOnlyList<string>> GetAllConfiguredStackBasesCoreAsync(CancellationToken cancellationToken)
    {
        var stacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serviceId in LocalAiStackHostUrls.WarmupServiceIds)
        {
            var stack = GetStackBaseForService(serviceId);
            if (stack is not null)
            {
                stacks.Add(stack);
            }
        }

        stacks.UnionWith(await GetRowOwnedLlamaStackBasesAsync(cancellationToken).ConfigureAwait(false));
        return stacks.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Distinct normalized <c>stackBaseUrl</c> values over active llama-cpp model rows.
    /// Rows without a row-owned stack target the global <c>LlamaCpp:BaseUrl</c> and are
    /// already covered by the config-based bases.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> GetRowOwnedLlamaStackBasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory!.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.Models
                .AsNoTracking()
                .Where(m => m.Provider == "llama-cpp" && m.IsActive && m.RuntimeConfigJson != null)
                .Select(m => m.RuntimeConfigJson)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in rows)
            {
                var stackBaseUrl = LocalRuntimeConfigurationParser.ParseStackBaseUrl(json);
                if (stackBaseUrl is not null
                    && LocalAiStackHostUrls.NormalizeStackBaseUrl(stackBaseUrl) is { } normalized)
                {
                    bases.Add(normalized);
                }
            }

            return bases;
        }
        catch (Exception ex)
        {
            // A transient DB failure must not take the whole warmup universe offline;
            // degrade to the config-declared stacks for this apply cycle.
            _logger?.LogWarning(ex, "Could not read row-owned llama-cpp stack bases; using config-declared stacks only.");
            return Array.Empty<string>();
        }
    }

    public string? GetStackBaseForService(string serviceId)
    {
        if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
        {
            return LocalAiStackHostUrls.NormalizeStackBaseUrl(_configuration["LlamaCpp:BaseUrl"]);
        }

        var configKey = ResolveLocalServiceHostConfigKey(serviceId);
        if (configKey is null)
        {
            return null;
        }

        return LocalAiStackHostUrls.NormalizeStackBaseUrl(_configuration[configKey]);
    }

    private static string? ResolveLocalServiceHostConfigKey(string serviceId) =>
        serviceId switch
        {
            RoutedServiceNames.SpeechTranscription =>
                $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings =>
                $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis =>
                $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration =>
                $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };
}

/// <summary>One configured AI instance: canonical identity + the base used for transport.</summary>
public sealed record LocalAiInstance(string CanonicalKey, string Base);