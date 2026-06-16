namespace AntRunner.Chat.OpenRouter;

public static class OpenRouterAttribution
{
    public const string HttpReferer = "https://www.guideants.ai";
    public const string AppTitle = "GuideAnts";
    public const string AppCategories =
        "programming-app,cloud-agent,personal-agent,writing-assistant,general-chat,image-gen";

    public static void Apply(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("HTTP-Referer", HttpReferer);
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", AppTitle);
        request.Headers.TryAddWithoutValidation("X-Title", AppTitle);
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Categories", AppCategories);
    }
}
