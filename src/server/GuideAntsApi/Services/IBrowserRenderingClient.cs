namespace GuideAntsApi.Services;

public interface IBrowserRenderingClient
{
    Task<BrowserRenderedPageResult> RenderHtmlAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed record BrowserRenderedPageResult(
    bool IsSuccess,
    string? Html,
    string? Error,
    string? FinalUrl,
    int? StatusCode);
