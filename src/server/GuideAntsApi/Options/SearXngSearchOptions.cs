namespace GuideAntsApi.Options;

public sealed class SearXngSearchOptions
{
    public const string SectionName = "SearXngSearch";
    public const int DefaultTimeoutMs = 15000;
    public const int DefaultCount = 20;
    public const int DefaultSkip = 0;

    public string BaseUrl { get; set; } = "http://127.0.0.1:8091";
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;
    public string Language { get; set; } = "en-US";
    public int SafeSearch { get; set; } = 1;
    public string Categories { get; set; } = "general";
}
