using GuideAntsApi.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Probes the DocumentServer container so the UI only offers "New Word/Excel/PowerPoint"
/// when the editor can actually open the file. Successes are never cached; failures are
/// cached for a couple of seconds so a hung container doesn't stall every menu open.
/// </summary>
public sealed class DocumentServerHealthProbe : IDocumentServerHealthProbe
{
    internal const string CacheKey = "documentserver:reachable";
    // /info/info.json is blocked in some deployments; the editor API script is always served.
    internal const string ProbePath = "/web-apps/apps/api/documents/api.js";
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);
    // Only failures are cached: a success is never stale, so stopping the container is seen on the
    // next call, while a hung container costs one probe timeout per window instead of one per call.
    internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptions<DocumentServerOptions> _options;
    private readonly ILogger<DocumentServerHealthProbe> _logger;
    private readonly TimeSpan _probeTimeout;

    public DocumentServerHealthProbe(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<DocumentServerOptions> options,
        ILogger<DocumentServerHealthProbe> logger)
        : this(httpClientFactory, cache, options, logger, DefaultProbeTimeout)
    {
    }

    internal DocumentServerHealthProbe(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<DocumentServerOptions> options,
        ILogger<DocumentServerHealthProbe> logger,
        TimeSpan probeTimeout)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
        _probeTimeout = probeTimeout;
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        var documentServer = _options.Value;
        if (!documentServer.Enabled)
        {
            return false;
        }

        if (_cache.TryGetValue(CacheKey, out bool cachedFailure) && !cachedFailure)
        {
            return false;
        }

        var reachable = await ProbeAsync(documentServer.InternalUrl, cancellationToken).ConfigureAwait(false);
        if (!reachable)
        {
            _cache.Set(CacheKey, false, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = FailureCacheTtl,
                Size = 1
            });
        }
        else
        {
            _cache.Remove(CacheKey);
        }

        return reachable;
    }

    private async Task<bool> ProbeAsync(string internalUrl, CancellationToken cancellationToken)
    {
        var probeUrl = $"{internalUrl.TrimEnd('/')}{ProbePath}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_probeTimeout);

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client
                .GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "DocumentServer probe returned HTTP {StatusCode}. url={ProbeUrl}",
                    (int)response.StatusCode,
                    LogValueSanitizer.Sanitize(probeUrl));
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeouts, refused connections and bad URLs all mean "not live". A caller
            // cancellation is not a verdict on DocumentServer, so it propagates uncached.
            _logger.LogDebug(ex, "DocumentServer probe failed. url={ProbeUrl}", LogValueSanitizer.Sanitize(probeUrl));
            return false;
        }
    }
}
