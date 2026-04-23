using HtmlAgility;
using HtmlAgilityPack;

namespace GuideAntsApi.Services;

public class WebScrapingService : IWebScrapingService
{
    private const int MinimumMarkdownLength = 128;

    private readonly ILogger<WebScrapingService> _logger;
    private readonly IExcludedHostService _excludedHostService;
    private readonly IBrowserRenderingClient _browserRenderingClient;
    private readonly Func<string, Task<MarkdownConversionResult>> _htmlAgilityFetch;
    private readonly SemaphoreSlim _semaphore = new(10);

    public WebScrapingService(
        ILogger<WebScrapingService> logger,
        IExcludedHostService excludedHostService,
        IBrowserRenderingClient browserRenderingClient,
        Func<string, Task<MarkdownConversionResult>>? htmlAgilityFetch = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _excludedHostService = excludedHostService ?? throw new ArgumentNullException(nameof(excludedHostService));
        _browserRenderingClient = browserRenderingClient ?? throw new ArgumentNullException(nameof(browserRenderingClient));
        _htmlAgilityFetch = htmlAgilityFetch ?? HtmlAgilityPackExtensions.ConvertUrlToMarkdownAsync;
    }

    public async Task<string> GetPageContentAsync(string url)
    {
        if (!TryNormalizeUrl(url, out var normalizedUrl, out var normalizedUri))
            throw new ArgumentException("Invalid URL. Only absolute HTTP/HTTPS URLs are supported.", nameof(url));

        await _semaphore.WaitAsync();
        try
        {
            _logger.LogInformation("Reading markdown content for {Url}", normalizedUrl);

            var excludedHosts = await _excludedHostService.GetExcludedHostsAsync();
            if (_excludedHostService.IsHostExcluded(normalizedUri.Host, excludedHosts))
            {
                return $"Host is excluded from reading: {normalizedUri.Host}";
            }

            var htmlAgilityResult = await _htmlAgilityFetch(normalizedUrl);
            var htmlAgilityMarkdown = NormalizeMarkdown(htmlAgilityResult.Content);
            if (IsUsableMarkdown(htmlAgilityMarkdown))
            {
                return htmlAgilityMarkdown;
            }

            var browserResult = await _browserRenderingClient.RenderHtmlAsync(normalizedUri, CancellationToken.None);

            if (IsExclusionWorthyStatusCode(browserResult.StatusCode))
            {
                var hostForExclusion = normalizedUri.Host;
                if (!string.IsNullOrWhiteSpace(browserResult.FinalUrl) &&
                    Uri.TryCreate(browserResult.FinalUrl, UriKind.Absolute, out var finalUri))
                {
                    hostForExclusion = finalUri.Host;
                }

                await _excludedHostService.TryAddExcludedHostAsync(
                    hostForExclusion,
                    $"Browser render HTTP {browserResult.StatusCode}");
            }

            if (browserResult.IsSuccess && !string.IsNullOrWhiteSpace(browserResult.Html))
            {
                var document = new HtmlDocument();
                document.LoadHtml(browserResult.Html);

                var conversion = document.ConvertToMarkdown(normalizedUri);
                var browserMarkdown = NormalizeMarkdown(conversion.Content);
                if (IsUsableMarkdown(browserMarkdown))
                {
                    return browserMarkdown;
                }

                return $"Page was not readable: Markdown too short ({browserMarkdown.Length} < {MinimumMarkdownLength})";
            }

            var readableError = string.IsNullOrWhiteSpace(browserResult.Error)
                ? "unknown error"
                : browserResult.Error.Trim();

            return $"Page was not readable: {readableError}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read markdown content for {Url}", normalizedUrl);
            return $"Page was not readable: {ex.Message}";
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string NormalizeMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        return markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static bool IsUsableMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return false;

        if (markdown.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return false;

        return markdown.Length >= MinimumMarkdownLength;
    }

    private static bool TryNormalizeUrl(string? rawUrl, out string normalizedUrl, out Uri normalizedUri)
    {
        normalizedUrl = string.Empty;
        normalizedUri = default!;

        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        normalizedUri = uri;
        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool IsExclusionWorthyStatusCode(int? statusCode)
        => statusCode is 401 or 403 or 451;
}
