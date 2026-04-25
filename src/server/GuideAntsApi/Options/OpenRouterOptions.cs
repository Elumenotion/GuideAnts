namespace GuideAntsApi.Options;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string? HttpReferer { get; set; }
    public string? AppTitle { get; set; }
}
