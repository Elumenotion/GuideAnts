using AntRunner.Chat.Anthropic;
using AntRunner.Chat.GoogleGemini;
using AntRunner.Chat.HuggingFace;
using AntRunner.Chat.OpenAI;
using AntRunner.Chat.OpenRouter;
using GuideAntsApi.Options;

namespace GuideAntsApi.Settings;

public interface IProviderConfigurationResolver
{
    AzureOpenAiConfig GetAzureOpenAiConfig();

    /// <summary>
    /// Config accessor for the standalone OpenAI platform (api.openai.com) — the
    /// <c>OpenAI:*</c> section in appsettings / DB-backed settings. Distinct
    /// from <see cref="GetAzureOpenAiConfig"/> because catalog rows with
    /// provider <c>openai-chat</c> / <c>openai-responses</c> target the OpenAI
    /// platform, and rows with <c>azure-openai-chat</c> /
    /// <c>azure-openai-responses</c> target Azure OpenAI. Each provider string
    /// has exactly one backing provider section; crossing the wires is the bug
    /// that motivated splitting this out.
    /// </summary>
    AzureOpenAiConfig GetOpenAiConfig();
    OpenRouterChatConfig GetOpenRouterChatConfig();
    HuggingFaceChatConfig GetHuggingFaceChatConfig();
    GoogleGeminiChatConfig GetGoogleGeminiChatConfig();
    GoogleGeminiApiConfig GetGoogleGeminiApiConfig();
    OpenRouterOptions GetOpenRouterOptions();
    HuggingFaceRouterOptions GetHuggingFaceOptions();

    AnthropicConfig GetAnthropicConfig();
    string? ResolveConfigurationVariableName(string variableName);
}

public sealed class ProviderConfigurationResolver(IConfiguration configuration) : IProviderConfigurationResolver
{
    private readonly IConfiguration _configuration = configuration;

    public AzureOpenAiConfig GetAzureOpenAiConfig()
    {
        return new AzureOpenAiConfig
        {
            ResourceName = GetValue("AzureOpenAI:Resource"),
            ApiKey = GetValue("AzureOpenAI:ApiKey"),
            ApiVersion = GetValue("AzureOpenAI:ApiVersion"),
            DeploymentId = null
        };
    }

    public AzureOpenAiConfig GetOpenAiConfig()
    {
        // OpenAI platform (api.openai.com): ResourceName is intentionally left
        // null so the OpenAI client factories select platform settings instead
        // of the Azure OpenAI resource constructor. The platform has no
        // api-version concept, so ApiVersion is not read here.
        return new AzureOpenAiConfig
        {
            ResourceName = null,
            ApiKey = GetValue("OpenAI:ApiKey"),
            ApiVersion = null,
            DeploymentId = null
        };
    }

    public OpenRouterChatConfig GetOpenRouterChatConfig()
    {
        return new OpenRouterChatConfig
        {
            ApiKey = GetValue("OpenRouter:ApiKey") ?? string.Empty,
            BaseUrl = GetValue("OpenRouter:BaseUrl") ?? "https://openrouter.ai/api/v1",
            HttpReferer = GetValue("OpenRouter:HttpReferer"),
            AppTitle = GetValue("OpenRouter:AppTitle")
        };
    }

    public HuggingFaceChatConfig GetHuggingFaceChatConfig()
    {
        return new HuggingFaceChatConfig
        {
            Token = GetValue("HuggingFace:Token") ?? string.Empty,
            RouterBaseUrl = GetValue("HuggingFace:RouterBaseUrl") ?? "https://router.huggingface.co/v1"
        };
    }

    public GoogleGeminiApiConfig GetGoogleGeminiApiConfig()
    {
        return new GoogleGeminiApiConfig
        {
            ApiKey = GetValue("GoogleGeminiApi:ApiKey")
        };
    }

    public GoogleGeminiChatConfig GetGoogleGeminiChatConfig()
    {
        return new GoogleGeminiChatConfig
        {
            ApiKey = GetValue("GoogleGeminiApi:ApiKey")
        };
    }

    public OpenRouterOptions GetOpenRouterOptions()
    {
        return new OpenRouterOptions
        {
            ApiKey = GetValue("OpenRouter:ApiKey") ?? string.Empty,
            BaseUrl = GetValue("OpenRouter:BaseUrl") ?? "https://openrouter.ai/api/v1",
            HttpReferer = GetValue("OpenRouter:HttpReferer"),
            AppTitle = GetValue("OpenRouter:AppTitle")
        };
    }

    public HuggingFaceRouterOptions GetHuggingFaceOptions()
    {
        return new HuggingFaceRouterOptions
        {
            Token = GetValue("HuggingFace:Token") ?? string.Empty,
            RouterBaseUrl = GetValue("HuggingFace:RouterBaseUrl") ?? "https://router.huggingface.co/v1"
        };
    }

    public AnthropicConfig GetAnthropicConfig()
    {
        return new AnthropicConfig
        {
            BaseUrl = GetValue("Anthropic:BaseUrl"),
            ApiKey = GetValue("Anthropic:ApiKey"),
            AuthToken = GetValue("Anthropic:AuthToken"),
            DefaultModel = null,
            DefaultMaxTokens = 64000,
            ThinkingBudgets = new AnthropicThinkingBudgets()
        };
    }

    public string? ResolveConfigurationVariableName(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return null;
        }

        return _configuration[variableName];
    }

    private string? GetValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = _configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private int GetIntValue(int defaultValue, params string[] keys)
    {
        var raw = GetValue(keys);
        return int.TryParse(raw, out var value) && value > 0
            ? value
            : defaultValue;
    }

    private int? GetNullableIntValue(params string[] keys)
    {
        var raw = GetValue(keys);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, out var value) && value > 0
            ? value
            : null;
    }
}

public sealed record GoogleGeminiApiConfig
{
    public string? ApiKey { get; init; }
}

public sealed record HuggingFaceRouterOptions
{
    public string Token { get; init; } = string.Empty;
    public string RouterBaseUrl { get; init; } = "https://router.huggingface.co/v1";
}
