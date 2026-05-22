using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GuideAntsApi.Models;
using GuideAntsApi.Options;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services;

public sealed class SearXngWebSearchService(
    HttpClient httpClient,
    IOptions<SearXngSearchOptions> options,
    IExcludedHostService excludedHostService,
    ILogger<SearXngWebSearchService> logger) : IWebSearchService
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly SearXngSearchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IExcludedHostService _excludedHostService = excludedHostService ?? throw new ArgumentNullException(nameof(excludedHostService));
    private readonly ILogger<SearXngWebSearchService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<WebSearchToolResponse> SearchAsync(
        string query,
        int count = SearXngSearchOptions.DefaultCount,
        int skip = SearXngSearchOptions.DefaultSkip,
        CancellationToken cancellationToken = default)
    {
        var requestStopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query is required.", nameof(query));

        count = Math.Max(1, count);
        skip = Math.Max(0, skip);

        var excludedHosts = await _excludedHostService.GetExcludedHostsAsync(cancellationToken);
        var desiredResultCount = skip + count;
        var estimatedPages = Math.Max(1, (int)Math.Ceiling(desiredResultCount / 10d));
        var maxPagesToQuery = estimatedPages + 1;

        var collected = new List<WebSearchResultItem>(desiredResultCount);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var pageNumber = 1; pageNumber <= maxPagesToQuery && collected.Count < desiredResultCount; pageNumber++)
        {
            var pageResults = await QueryPageAsync(query, pageNumber, cancellationToken);
            if (pageResults.Count == 0)
                break;

            foreach (var result in pageResults)
            {
                if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
                    continue;

                if (_excludedHostService.IsHostExcluded(uri.Host, excludedHosts))
                    continue;

                if (!seenUrls.Add(uri.AbsoluteUri))
                    continue;

                collected.Add(result with { Url = uri.AbsoluteUri });
                if (collected.Count >= desiredResultCount)
                    break;
            }
        }

        var selected = collected.Skip(skip).Take(count).ToList();

        var meta = new WebSearchTelemetry(
            ProfileLabel: "searxng-http",
            AttemptUsed: 1,
            PoolMode: "api",
            RequestTotalMs: requestStopwatch.ElapsedMilliseconds,
            PagesParsed: Math.Min(maxPagesToQuery, Math.Max(1, estimatedPages)),
            NextHops: Math.Max(0, Math.Min(maxPagesToQuery, Math.Max(1, estimatedPages)) - 1));

        _logger.LogInformation(
            "searXNG search completed with {Count} results for query '{Query}'.",
            selected.Count,
            query);

        return new WebSearchToolResponse(
            Type: "WebSearch",
            Web: new WebSearchResultsEnvelope("web_results", selected),
            FamilyFriendly: _options.SafeSearch > 0,
            Meta: meta);
    }

    private async Task<IReadOnlyList<WebSearchResultItem>> QueryPageAsync(
        string query,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _options.TimeoutMs)));

        var requestUri = BuildSearchUri(query, pageNumber);

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"searXNG request failed with status code {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            if (!document.RootElement.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<WebSearchResultItem>();

            foreach (var item in resultsElement.EnumerateArray())
            {
                var title = GetStringProperty(item, "title");
                var url = GetStringProperty(item, "url");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                    continue;

                var snippet = GetStringProperty(item, "content")
                    ?? GetStringProperty(item, "description")
                    ?? string.Empty;

                results.Add(new WebSearchResultItem(title, url, snippet));
            }

            return results;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"searXNG request timed out after {_options.TimeoutMs}ms.");
        }
    }

    private Uri BuildSearchUri(string query, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("SearXngSearch:BaseUrl is required.");
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var queryParts = new[]
        {
            $"q={Uri.EscapeDataString(query)}",
            "format=json",
            $"pageno={pageNumber.ToString(CultureInfo.InvariantCulture)}",
            $"language={Uri.EscapeDataString(_options.Language)}",
            $"safesearch={_options.SafeSearch.ToString(CultureInfo.InvariantCulture)}",
            $"categories={Uri.EscapeDataString(_options.Categories)}"
        };

        return new Uri($"{baseUrl}/search?{string.Join("&", queryParts)}");
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString()?.Trim();
    }
}
