using System.Net.Http.Json;
using GuideAntsApi.Options;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services;

public sealed class SearXngBrowserRenderingClient(
    HttpClient httpClient,
    IOptions<BrowserRenderingOptions> options,
    ILogger<SearXngBrowserRenderingClient> logger) : IBrowserRenderingClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly BrowserRenderingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<SearXngBrowserRenderingClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<BrowserRenderedPageResult> RenderHtmlAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                _options.RenderHtmlPath,
                new BrowserRenderRequest(uri.AbsoluteUri),
                cancellationToken);

            BrowserRenderResponse? payload = null;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<BrowserRenderResponse>(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse browser rendering response for {Url}", uri);
            }

            if (payload is not null)
            {
                return new BrowserRenderedPageResult(
                    payload.IsSuccess,
                    payload.Html,
                    payload.Error,
                    payload.FinalUrl,
                    payload.StatusCode);
            }

            var fallbackError = response.IsSuccessStatusCode
                ? "Browser rendering returned an unreadable response"
                : $"HTTP {(int)response.StatusCode}";

            return new BrowserRenderedPageResult(
                false,
                null,
                fallbackError,
                uri.AbsoluteUri,
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BrowserRenderedPageResult(false, null, "Browser rendering timed out", uri.AbsoluteUri, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser rendering failed for {Url}", uri);
            return new BrowserRenderedPageResult(false, null, ex.Message, uri.AbsoluteUri, null);
        }
    }

    private sealed record BrowserRenderRequest(string Url);

    private sealed record BrowserRenderResponse(
        bool IsSuccess,
        string? Html,
        string? Error,
        string? FinalUrl,
        int? StatusCode);
}
