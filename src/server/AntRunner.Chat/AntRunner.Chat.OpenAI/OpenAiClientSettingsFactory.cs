using OpenAI;

namespace AntRunner.Chat.OpenAI;

internal static class OpenAiClientSettingsFactory
{
    public static OpenAISettings Create(AzureOpenAiConfig config, string? deploymentId)
    {
        if (string.IsNullOrWhiteSpace(config.ResourceName))
        {
            return string.IsNullOrWhiteSpace(config.ApiVersion)
                ? new OpenAISettings()
                : new OpenAISettings(domain: "api.openai.com", apiVersion: config.ApiVersion);
        }

        return new OpenAISettings(
            resourceName: config.ResourceName,
            deploymentId: deploymentId,
            apiVersion: config.ApiVersion);
    }
}
