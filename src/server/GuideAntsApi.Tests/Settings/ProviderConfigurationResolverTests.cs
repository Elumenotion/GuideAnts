using FluentAssertions;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ProviderConfigurationResolverTests
{
    [TestMethod]
    public void Resolves_provider_sections_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Resource"] = "my-resource",
                ["AzureOpenAI:ApiKey"] = "azure-key",
                ["AzureOpenAI:ApiVersion"] = "2024-10-01",
                ["OpenAI:ApiKey"] = "openai-key",
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:BaseUrl"] = "https://openrouter.test/v1",
                ["HuggingFace:Token"] = "hf-token",
                ["GoogleGeminiApi:ApiKey"] = "gemini-key",
                ["Anthropic:ApiKey"] = "anthropic-key",
                ["Anthropic:BaseUrl"] = "https://anthropic.test",
                ["Custom:Setting"] = "custom-value"
            })
            .Build();

        var resolver = new ProviderConfigurationResolver(configuration);

        resolver.GetAzureOpenAiConfig().ResourceName.Should().Be("my-resource");
        resolver.GetAzureOpenAiConfig().ApiKey.Should().Be("azure-key");
        resolver.GetOpenAiConfig().ApiKey.Should().Be("openai-key");
        resolver.GetOpenAiConfig().ResourceName.Should().BeNull();
        resolver.GetOpenRouterChatConfig().ApiKey.Should().Be("or-key");
        resolver.GetOpenRouterChatConfig().BaseUrl.Should().Be("https://openrouter.test/v1");
        resolver.GetHuggingFaceChatConfig().Token.Should().Be("hf-token");
        resolver.GetGoogleGeminiChatConfig().ApiKey.Should().Be("gemini-key");
        resolver.GetAnthropicConfig().ApiKey.Should().Be("anthropic-key");
        resolver.ResolveConfigurationVariableName("Custom:Setting").Should().Be("custom-value");
        resolver.ResolveConfigurationVariableName(" ").Should().BeNull();
    }
}
