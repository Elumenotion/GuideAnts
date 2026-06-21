using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.Infrastructure;

/// <summary>
/// Phase E (R-5.7): runs Infrastructure tab probes against URL and filesystem
/// targets supplied by the caller. Uses <see cref="IHttpClientFactory"/> so
/// HttpClient lifetime is managed by ASP.NET Core. URL probes use
/// <c>GET</c> with <c>Range: bytes=0-0</c> (not <c>HEAD</c>): many inference
/// stacks hang on <c>HEAD</c> for paths like <c>/llama-cpp</c> while answering
/// ranged <c>GET</c>s immediately. 3-second budget; malformed URLs short-circuit.
/// <para>
/// Items with id <c>LlamaCpp:BaseUrl</c> are probed at <c>{BaseUrl}/health</c>
/// (gateway: <c>/llama-cpp/health</c>), not the bare OpenAI-compatible base — the
/// base path alone can redirect, stall, or omit a fast ranged response.
/// </para>
/// <para>
/// Items with id <c>LocalServiceHosts:DocumentIntelligenceBaseUrl</c> are probed at
/// <c>{BaseUrl}/version</c> so the Infrastructure tab validates docling-serve readiness.
/// </para>
/// <para>Individual item failures are captured per-row — this service never
/// throws, consistent with the non-fatal probe contract on
/// <see cref="IInfrastructureProbeService"/>.</para>
/// </summary>
public sealed class InfrastructureProbeService : IInfrastructureProbeService
{
    internal const string HttpClientName = "InfrastructureProbe";

    /// <summary>
    /// Per-probe timeout (R-5.7). Not user-configurable — the Infrastructure
    /// UI runs several probes in parallel on tab open; a tight bound keeps
    /// the worst-case render within a single human-perceptible beat.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public const string KindUrl = "url";
    public const string KindPath = "path";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InfrastructureProbeService> _logger;

    public InfrastructureProbeService(
        IHttpClientFactory httpClientFactory,
        ILogger<InfrastructureProbeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<InfrastructureProbeBatchDto> ProbeAsync(
        IReadOnlyCollection<InfrastructureProbeRequestItemDto> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var results = new List<InfrastructureProbeResultDto>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item is null
                || string.IsNullOrWhiteSpace(item.Id)
                || string.IsNullOrWhiteSpace(item.Kind))
            {
                results.Add(new InfrastructureProbeResultDto(
                    Id: item?.Id ?? string.Empty,
                    Kind: item?.Kind ?? string.Empty,
                    Target: item?.Target ?? string.Empty,
                    LatencyMs: null,
                    Error: "invalid probe item: id and kind are required",
                    Reachable: null,
                    StatusCode: null,
                    Exists: null,
                    Writable: null));
                continue;
            }

            if (string.Equals(item.Kind, KindUrl, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(await ProbeUrlAsync(item, cancellationToken));
            }
            else if (string.Equals(item.Kind, KindPath, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(ProbeFilesystem(item));
            }
            else
            {
                results.Add(new InfrastructureProbeResultDto(
                    Id: item.Id,
                    Kind: item.Kind,
                    Target: item.Target ?? string.Empty,
                    LatencyMs: null,
                    Error: $"unsupported probe kind '{item.Kind}'",
                    Reachable: null,
                    StatusCode: null,
                    Exists: null,
                    Writable: null));
            }
        }

        return new InfrastructureProbeBatchDto(DateTime.UtcNow, results);
    }

    private async Task<InfrastructureProbeResultDto> ProbeUrlAsync(
        InfrastructureProbeRequestItemDto item,
        CancellationToken cancellationToken)
    {
        var target = item.Target ?? string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {
            return UrlResult(item, null, null, "value is empty", reachable: false);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return UrlResult(item, null, null, "value is not a valid http(s) URL", reachable: false);
        }

        if (string.Equals(item.Id, ServiceRoutingContracts.LlamaBaseUrlKey, StringComparison.OrdinalIgnoreCase))
        {
            uri = ToLlamaCppHealthProbeUri(uri);
        }
        else if (string.Equals(item.Id, ServiceRoutingContracts.DocumentIntelligenceBaseUrlKey, StringComparison.OrdinalIgnoreCase))
        {
            uri = ToDoclingVersionProbeUri(uri);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(ProbeTimeout);

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        httpClient.Timeout = Timeout.InfiniteTimeSpan; // we own the cancellation budget

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await SendAsync(httpClient, HttpMethod.Get, uri, rangeHeader: true, linkedCts.Token);
            stopwatch.Stop();
            var statusCode = (int)response.StatusCode;
            var reachable = statusCode is >= 100 and < 500;

            return UrlResult(
                item,
                statusCode: statusCode,
                latencyMs: (int)stopwatch.Elapsed.TotalMilliseconds,
                error: reachable ? null : $"HTTP {statusCode} {response.ReasonPhrase}",
                reachable: reachable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return UrlResult(
                item,
                statusCode: null,
                latencyMs: (int)stopwatch.Elapsed.TotalMilliseconds,
                error: $"probe timed out after {ProbeTimeout.TotalSeconds:F0}s",
                reachable: false);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogDebug(ex, "Infrastructure probe HTTP failure for {Id} -> {Target}", item.Id, target);
            var reason = ex.StatusCode is HttpStatusCode status ? $"HTTP {(int)status}" : ex.Message;
            return UrlResult(
                item,
                statusCode: ex.StatusCode is HttpStatusCode s ? (int)s : null,
                latencyMs: (int)stopwatch.Elapsed.TotalMilliseconds,
                error: reason,
                reachable: false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Infrastructure probe unexpected failure for {Id} -> {Target}", item.Id, target);
            return UrlResult(
                item,
                statusCode: null,
                latencyMs: (int)stopwatch.Elapsed.TotalMilliseconds,
                error: ex.Message,
                reachable: false);
        }
    }

    /// <summary>
    /// Maps the configured OpenAI-compatible base (e.g. <c>http://host/llama-cpp</c>)
    /// to the llama-server health route behind the gateway (<c>.../llama-cpp/health</c>).
    /// </summary>
    internal static Uri ToLlamaCppHealthProbeUri(Uri configuredBase)
    {
        var builder = new UriBuilder(configuredBase);
        var path = builder.Path;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            builder.Path = ServiceRoutingContracts.LlamaHealthPath;
            return builder.Uri;
        }

        var trimmed = path.TrimEnd('/');
        builder.Path = trimmed + "/health";
        return builder.Uri;
    }

    /// <summary>
    /// Maps the configured docling-serve base (e.g. <c>http://docling-serve:5001</c>)
    /// to the version endpoint (<c>.../version</c>).
    /// </summary>
    internal static Uri ToDoclingVersionProbeUri(Uri configuredBase)
    {
        var builder = new UriBuilder(configuredBase);
        var path = builder.Path;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            builder.Path = ServiceRoutingContracts.DoclingVersionPath;
            return builder.Uri;
        }

        var trimmed = path.TrimEnd('/');
        builder.Path = trimmed + ServiceRoutingContracts.DoclingVersionPath;
        return builder.Uri;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpMethod method,
        Uri uri,
        bool rangeHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (rangeHeader)
        {
            request.Headers.Range = new RangeHeaderValue(0, 0);
        }

        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static InfrastructureProbeResultDto UrlResult(
        InfrastructureProbeRequestItemDto item,
        int? statusCode,
        int? latencyMs,
        string? error,
        bool reachable)
    {
        return new InfrastructureProbeResultDto(
            Id: item.Id,
            Kind: KindUrl,
            Target: item.Target ?? string.Empty,
            LatencyMs: latencyMs,
            Error: error,
            Reachable: reachable,
            StatusCode: statusCode,
            Exists: null,
            Writable: null);
    }

    private static InfrastructureProbeResultDto ProbeFilesystem(InfrastructureProbeRequestItemDto item)
    {
        var target = item.Target ?? string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {
            return PathResult(item, exists: false, writable: false, latencyMs: 0, error: "value is empty");
        }

        try
        {
            if (File.Exists(target))
            {
                // Files are not writability-probed: creating a sentinel file next
                // to an arbitrary file would imply directory-level write access,
                // which is the directory case below. Report writable=false so the
                // UI can distinguish "exists" from "exists + directory-writable".
                return PathResult(item, exists: true, writable: false, latencyMs: 0, error: null);
            }

            if (Directory.Exists(target))
            {
                var writable = TryProbeDirectoryWritability(target, out var writeError);
                return PathResult(
                    item,
                    exists: true,
                    writable: writable,
                    latencyMs: 0,
                    error: writable ? null : writeError);
            }

            return PathResult(item, exists: false, writable: false, latencyMs: 0, error: "path does not exist");
        }
        catch (Exception ex)
        {
            return PathResult(item, exists: null, writable: null, latencyMs: 0, error: ex.Message);
        }
    }

    private static bool TryProbeDirectoryWritability(string directory, out string? error)
    {
        var sentinelPath = Path.Combine(directory, $".probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(sentinelPath, bufferSize: 1, FileOptions.DeleteOnClose))
            {
                // no-op: file is removed on close
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"directory exists but is not writable: {ex.Message}";
            return false;
        }
    }

    private static InfrastructureProbeResultDto PathResult(
        InfrastructureProbeRequestItemDto item,
        bool? exists,
        bool? writable,
        int? latencyMs,
        string? error)
    {
        return new InfrastructureProbeResultDto(
            Id: item.Id,
            Kind: KindPath,
            Target: item.Target ?? string.Empty,
            LatencyMs: latencyMs,
            Error: error,
            Reachable: null,
            StatusCode: null,
            Exists: exists,
            Writable: writable);
    }
}
