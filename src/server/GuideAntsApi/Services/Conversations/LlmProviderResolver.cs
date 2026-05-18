using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.Services.Conversations;

internal static class LlmProviderResolver
{
    public static string ResolveUsageServiceName(
        string? modelDeploymentId,
        IServiceScopeFactory? scopeFactory = null)
    {
        var provider = ResolveProvider(modelDeploymentId, scopeFactory);
        return provider switch
        {
            LlmProvider.Anthropic => "Anthropic",
            LlmProvider.LlamaCpp => "LocalLlamaCpp",
            LlmProvider.OpenAiPlatform => "OpenAI",
            LlmProvider.AzureOpenAi => "AzureOpenAI",
            LlmProvider.GoogleGeminiChat => "GoogleGemini",
            LlmProvider.HuggingFaceInference => "HuggingFace",
            LlmProvider.OpenRouterChat => "OpenRouter",
            _ => throw new InvalidOperationException(
                $"Unmapped LlmProvider '{provider}' in ResolveUsageServiceName.")
        };
    }

    private static LlmProvider ResolveProvider(
        string? modelDeploymentId,
        IServiceScopeFactory? scopeFactory)
    {
        if (string.IsNullOrWhiteSpace(modelDeploymentId))
        {
            throw new InvalidOperationException("Model deployment id is required for provider resolution.");
        }

        if (scopeFactory == null)
        {
            throw new InvalidOperationException("Service scope factory is required for provider resolution.");
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var model = db.Models
            .AsNoTracking()
            .FirstOrDefault(m => m.ModelId == modelDeploymentId);

        if (model == null)
        {
            throw new InvalidOperationException(
                $"Model '{modelDeploymentId}' not found in model catalog.");
        }

        if (string.IsNullOrWhiteSpace(model.Provider))
        {
            throw new InvalidOperationException(
                $"Model '{modelDeploymentId}' is missing provider configuration.");
        }

        return ParseProvider(model.Provider);
    }

    private static LlmProvider ParseProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "anthropic" => LlmProvider.Anthropic,
            "llama-cpp" => LlmProvider.LlamaCpp,
            "openai-chat" => LlmProvider.OpenAiPlatform,
            "openai-responses" => LlmProvider.OpenAiPlatform,
            "azure-openai-chat" => LlmProvider.AzureOpenAi,
            "azure-openai-responses" => LlmProvider.AzureOpenAi,
            "google-gemini-chat" => LlmProvider.GoogleGeminiChat,
            "hf-inference-chat" => LlmProvider.HuggingFaceInference,
            "openrouter-chat" => LlmProvider.OpenRouterChat,
            _ => throw new InvalidOperationException(
                $"Unsupported model provider '{provider}'. Expected one of: openai-chat, openai-responses, azure-openai-chat, azure-openai-responses, anthropic, llama-cpp, google-gemini-chat, hf-inference-chat, openrouter-chat.")
        };
    }

    private enum LlmProvider
    {
        OpenAiPlatform,
        AzureOpenAi,
        Anthropic,
        LlamaCpp,
        GoogleGeminiChat,
        HuggingFaceInference,
        OpenRouterChat
    }
}
