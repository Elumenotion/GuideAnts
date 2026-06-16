using AntRunner.ToolCalling.Attributes;
using HtmlAgility;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;

namespace GuideAntsApi.Services;

public static class ReadWebTools
{
    private const int DirectFetchTimeoutSeconds = 5;
    private const int BrowserRenderTimeoutSeconds = 8;
    private const int MaxHtmlCharacters = 10_000_000;

    private static IServiceProvider? _serviceProvider;

    public static void InitializeServiceProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    [Tool(
        OperationId = "GetContentFromUrl",
        Summary = "Reads a web page and returns markdown of the content"
    )]
    public static async Task<MarkdownConversionResult> GetContentFromUrl(
        [Parameter(Description = "The absolute HTTP or HTTPS URL of the page to read.")]
        string url,

        [Parameter(Description = "Cancellation token", Hidden = true)]
        CancellationToken cancellationToken = default)
    {
        var errorResult = new MarkdownConversionResult();
        if (_serviceProvider == null)
        {
            errorResult.Content = "Error: ReadWeb tool services are not initialized.";
            return errorResult;
        }

        if (!TryCreateHttpUri(url, out var uri))
        {
            errorResult.Content = "Error: Invalid URL format. Only HTTP and HTTPS URLs are supported.";
            return errorResult;
        }

        await using var scope = _serviceProvider.CreateAsyncScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var browserRenderingClient = scope.ServiceProvider.GetRequiredService<IBrowserRenderingClient>();
        var excludedHostService = scope.ServiceProvider.GetRequiredService<IExcludedHostService>();
        var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?
            .CreateLogger(typeof(ReadWebTools).FullName!)
            ?? NullLogger.Instance;
        var totalStopwatch = Stopwatch.StartNew();

        logger.LogInformation("ReadWeb start for host {Host}.", uri.Host);

        var directFetch = await TryFetchDirectHtmlAsync(httpClientFactory, uri, cancellationToken);
        logger.LogInformation(
            "ReadWeb direct fetch completed for {Host}. StatusCode={StatusCode}, HasHtml={HasHtml}, Error={Error}.",
            uri.Host,
            directFetch.StatusCode,
            !string.IsNullOrWhiteSpace(directFetch.Html),
            directFetch.Error);

        if (!string.IsNullOrWhiteSpace(directFetch.Html))
        {
            logger.LogInformation("ReadWeb direct fetch succeeded for {Host} in {ElapsedMs}ms.", uri.Host, totalStopwatch.ElapsedMilliseconds);
            return ConvertHtmlToMarkdown(directFetch.Html, uri);
        }

        var rendered = await TryFetchRenderedHtmlAsync(browserRenderingClient, uri, cancellationToken);
        logger.LogInformation(
            "ReadWeb browser render completed for {Host}. StatusCode={StatusCode}, HasHtml={HasHtml}, Error={Error}.",
            uri.Host,
            rendered.StatusCode,
            !string.IsNullOrWhiteSpace(rendered.Html),
            rendered.Error);

        if (!string.IsNullOrWhiteSpace(rendered.Html))
        {
            var baseUri = TryCreateHttpUri(rendered.FinalUrl, out var finalUri) ? finalUri : uri;
            logger.LogInformation("ReadWeb browser render succeeded for {Host} in {ElapsedMs}ms.", uri.Host, totalStopwatch.ElapsedMilliseconds);
            return ConvertHtmlToMarkdown(rendered.Html, baseUri);
        }

        if (ShouldExcludeForAccessDenied(directFetch, rendered))
        {
            await excludedHostService.TryAddExcludedHostAsync(
                uri.Host,
                $"ReadWeb access denied. Direct status={directFetch.StatusCode?.ToString() ?? "none"}, Render status={rendered.StatusCode?.ToString() ?? "none"}, Render error={rendered.Error ?? "none"}.",
                cancellationToken);

            logger.LogInformation(
                "ReadWeb excluded host {Host} due to access denied. DirectStatus={DirectStatus}, RenderStatus={RenderStatus}, ElapsedMs={ElapsedMs}.",
                uri.Host,
                directFetch.StatusCode,
                rendered.StatusCode,
                totalStopwatch.ElapsedMilliseconds);
        }
        else
        {
            logger.LogInformation(
                "ReadWeb did not exclude host {Host}. Failure did not match access denied criteria. DirectStatus={DirectStatus}, RenderStatus={RenderStatus}, ElapsedMs={ElapsedMs}.",
                uri.Host,
                directFetch.StatusCode,
                rendered.StatusCode,
                totalStopwatch.ElapsedMilliseconds);
        }

        errorResult.Content = "Error: Request failed or timed out.";
        return errorResult;
    }

    private static async Task<DirectFetchResult> TryFetchDirectHtmlAsync(
        IHttpClientFactory httpClientFactory,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(DirectFetchTimeoutSeconds));

            var httpClient = httpClientFactory.CreateClient();
            using var response = await httpClient.GetAsync(uri, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new DirectFetchResult(null, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}");
            }

            var html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return IsProcessableHtml(html)
                ? new DirectFetchResult(html, (int)response.StatusCode, null)
                : new DirectFetchResult(null, (int)response.StatusCode, "HTML was empty or exceeded max size");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DirectFetchResult(null, null, "Direct fetch timed out");
        }
        catch
        {
            return new DirectFetchResult(null, null, "Direct fetch failed");
        }
    }

    private static async Task<RenderedFetchResult> TryFetchRenderedHtmlAsync(
        IBrowserRenderingClient browserRenderingClient,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(BrowserRenderTimeoutSeconds));

            var result = await browserRenderingClient.RenderHtmlAsync(uri, timeoutCts.Token);
            if (!result.IsSuccess || !IsProcessableHtml(result.Html))
            {
                return new RenderedFetchResult(null, result.FinalUrl, result.Error, result.StatusCode);
            }

            return new RenderedFetchResult(result.Html, result.FinalUrl, null, result.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RenderedFetchResult(null, null, "Browser rendering timed out", null);
        }
        catch (Exception ex)
        {
            return new RenderedFetchResult(null, null, ex.Message, null);
        }
    }

    private static MarkdownConversionResult ConvertHtmlToMarkdown(string html, Uri baseUri)
    {
        var errorResult = new MarkdownConversionResult();

        try
        {
            var htmlDocument = new HtmlDocument();
            htmlDocument.LoadHtml(html);
            return htmlDocument.ConvertToMarkdown(baseUri);
        }
        catch (Exception ex)
        {
            errorResult.Content = $"Error: Markdown conversion failed - {ex.Message}";
            return errorResult;
        }
    }

    private static bool IsProcessableHtml(string? html)
    {
        return !string.IsNullOrWhiteSpace(html) && html.Length <= MaxHtmlCharacters;
    }

    private static bool ShouldExcludeForAccessDenied(DirectFetchResult directFetch, RenderedFetchResult renderedFetch)
    {
        return IsAccessDenied(directFetch.StatusCode, directFetch.Error) ||
               IsAccessDenied(renderedFetch.StatusCode, renderedFetch.Error);
    }

    private static bool IsAccessDenied(int? statusCode, string? error)
    {
        if (statusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var normalized = error.Trim();
        return normalized.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateHttpUri(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private sealed record DirectFetchResult(string? Html, int? StatusCode, string? Error);
    private sealed record RenderedFetchResult(string? Html, string? FinalUrl, string? Error, int? StatusCode);
}
