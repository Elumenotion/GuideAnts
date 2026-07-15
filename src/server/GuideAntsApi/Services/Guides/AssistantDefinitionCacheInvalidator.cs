using AntRunner.Chat;

namespace GuideAntsApi.Services.Guides;

internal static class AssistantDefinitionCacheInvalidator
{
    public static void Invalidate(string assistantName)
    {
        AssistantUtility.ClearCache(assistantName);
        ThreadRun.ClearRequestBuilderCache(assistantName);
    }
}
