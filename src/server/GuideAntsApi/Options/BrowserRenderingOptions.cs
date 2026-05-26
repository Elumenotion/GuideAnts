namespace GuideAntsApi.Options;

public sealed class BrowserRenderingOptions
{
    public const string SectionName = "BrowserRendering";
    public const int DefaultTimeoutMs = 60000;

    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;
    public string RenderHtmlPath { get; set; } = "/browser/render-html";
}
