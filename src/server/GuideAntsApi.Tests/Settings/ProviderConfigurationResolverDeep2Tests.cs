using FluentAssertions;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ProviderConfigurationResolverDeep2Tests
{
    private static ProviderConfigurationResolver CreateResolver(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [TestMethod]
    public void GetOpenRouterChatConfig_FallsBackToDefaults_WhenKeysMissing()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        var config = resolver.GetOpenRouterChatConfig();

        config.ApiKey.Should().BeEmpty();
        config.BaseUrl.Should().Be("https://openrouter.ai/api/v1");
        config.HttpReferer.Should().BeNull();
        config.AppTitle.Should().BeNull();
    }

    [TestMethod]
    public void GetOpenRouterChatConfig_HonorsConfiguredOptionalFields()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["OpenRouter:ApiKey"] = "or-key",
            ["OpenRouter:BaseUrl"] = "https://openrouter.test/v1",
            ["OpenRouter:HttpReferer"] = "https://referer.test",
            ["OpenRouter:AppTitle"] = "Test App"
        });

        var config = resolver.GetOpenRouterChatConfig();

        config.ApiKey.Should().Be("or-key");
        config.BaseUrl.Should().Be("https://openrouter.test/v1");
        config.HttpReferer.Should().Be("https://referer.test");
        config.AppTitle.Should().Be("Test App");
    }

    [TestMethod]
    public void GetHuggingFaceChatConfig_FallsBackToDefaultRouterBaseUrl()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        var config = resolver.GetHuggingFaceChatConfig();

        config.Token.Should().BeEmpty();
        config.RouterBaseUrl.Should().Be("https://router.huggingface.co/v1");
    }

    [TestMethod]
    public void GetHuggingFaceOptions_ReflectsConfiguredValues()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["HuggingFace:Token"] = "hf-token",
            ["HuggingFace:RouterBaseUrl"] = "https://hf.test/v1"
        });

        var options = resolver.GetHuggingFaceOptions();

        options.Token.Should().Be("hf-token");
        options.RouterBaseUrl.Should().Be("https://hf.test/v1");
    }

    [TestMethod]
    public void GetHuggingFaceOptions_FallsBackToDefaults()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        var options = resolver.GetHuggingFaceOptions();

        options.Token.Should().BeEmpty();
        options.RouterBaseUrl.Should().Be("https://router.huggingface.co/v1");
    }

    [TestMethod]
    public void GetOpenRouterOptions_ReflectsConfiguredValues()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["OpenRouter:ApiKey"] = "or-key",
            ["OpenRouter:BaseUrl"] = "https://openrouter.test/v1",
            ["OpenRouter:HttpReferer"] = "https://referer.test",
            ["OpenRouter:AppTitle"] = "Title"
        });

        var options = resolver.GetOpenRouterOptions();

        options.ApiKey.Should().Be("or-key");
        options.BaseUrl.Should().Be("https://openrouter.test/v1");
        options.HttpReferer.Should().Be("https://referer.test");
        options.AppTitle.Should().Be("Title");
    }

    [TestMethod]
    public void GetOpenRouterOptions_FallsBackToDefaultBaseUrl()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        var options = resolver.GetOpenRouterOptions();

        options.ApiKey.Should().BeEmpty();
        options.BaseUrl.Should().Be("https://openrouter.ai/api/v1");
    }

    [TestMethod]
    public void GetGoogleGeminiApiConfig_ReadsApiKey()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["GoogleGeminiApi:ApiKey"] = "gemini-key"
        });

        resolver.GetGoogleGeminiApiConfig().ApiKey.Should().Be("gemini-key");
    }

    [TestMethod]
    public void GetGoogleGeminiApiConfig_ReturnsNull_WhenMissing()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        resolver.GetGoogleGeminiApiConfig().ApiKey.Should().BeNull();
    }

    [TestMethod]
    public void GetAnthropicConfig_ReadsBaseUrlApiKeyAndAuthToken()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["Anthropic:BaseUrl"] = "https://anthropic.test",
            ["Anthropic:ApiKey"] = "anthropic-key",
            ["Anthropic:AuthToken"] = "anthropic-token"
        });

        var config = resolver.GetAnthropicConfig();

        config.BaseUrl.Should().Be("https://anthropic.test");
        config.ApiKey.Should().Be("anthropic-key");
        config.AuthToken.Should().Be("anthropic-token");
        config.DefaultModel.Should().BeNull();
        config.DefaultMaxTokens.Should().Be(64000);
        config.ThinkingBudgets.Should().NotBeNull();
    }

    [TestMethod]
    public void GetAnthropicConfig_ReturnsNullsWhenUnset()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        var config = resolver.GetAnthropicConfig();

        config.BaseUrl.Should().BeNull();
        config.ApiKey.Should().BeNull();
        config.AuthToken.Should().BeNull();
    }

    [TestMethod]
    public void GetAzureOpenAiConfig_ReturnsNullValues_WhenSectionEmpty()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        var config = resolver.GetAzureOpenAiConfig();

        config.ResourceName.Should().BeNull();
        config.ApiKey.Should().BeNull();
        config.ApiVersion.Should().BeNull();
        config.DeploymentId.Should().BeNull();
    }

    [TestMethod]
    public void GetOpenAiConfig_LeavesResourceNameAndApiVersionNull()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "openai-key"
        });

        var config = resolver.GetOpenAiConfig();

        config.ApiKey.Should().Be("openai-key");
        config.ResourceName.Should().BeNull();
        config.ApiVersion.Should().BeNull();
    }

    [TestMethod]
    public void ResolveConfigurationVariableName_ReturnsNull_ForMissingKey()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>());

        resolver.ResolveConfigurationVariableName("Does:Not:Exist").Should().BeNull();
    }

    [TestMethod]
    public void ResolveConfigurationVariableName_ReturnsNull_ForNullOrWhitespace()
    {
        var resolver = CreateResolver(new Dictionary<string, string?>
        {
            ["Some:Key"] = "value"
        });

        resolver.ResolveConfigurationVariableName(string.Empty).Should().BeNull();
        resolver.ResolveConfigurationVariableName("   ").Should().BeNull();
    }
}
