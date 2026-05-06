using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ChatTargetValidatorTests
{
    [TestMethod]
    public void Validate_AcceptsOpenAiPlatform_WhenOpenAiSectionIsConfigured()
    {
        // "openai-chat" → OpenAI platform (api.openai.com). Only OpenAI:ApiKey
        // is required; ResourceName / Deployment are platform-specific no-ops.
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "sk-test"
        });

        Action act = () => validator.Validate(new ChatTarget("gpt-4.1", "openai-chat", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_AcceptsAzureOpenAi_WhenAzureSectionIsConfigured()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Resource"] = "test-resource",
            ["AzureOpenAI:ApiKey"] = "test-key"
        });

        Action act = () => validator.Validate(new ChatTarget("gpt-4.1", "azure-openai-chat", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_AcceptsAzureOpenAiResponses_WhenAzureSectionIsConfigured()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Resource"] = "test-resource",
            ["AzureOpenAI:ApiKey"] = "test-key"
        });

        Action act = () => validator.Validate(new ChatTarget("o3", "azure-openai-responses", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_Throws_ProviderNotReady_WhenOpenAiPlatformSectionMissing()
    {
        // A row labeled "openai-chat" must probe OpenAI:* (platform), not
        // AzureOpenAI:*. If OpenAI:ApiKey is unset we expect a
        // ProviderNotReady blocker pointing at the OpenAI section.
        var validator = CreateValidator(new Dictionary<string, string?>());

        Action act = () => validator.Validate(new ChatTarget("gpt-4.1", "openai-chat", RuntimeConfigJson: null));
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ProviderNotReady
                         && ex.ProviderSection == "OpenAI"
                         && ex.Blockers.Count >= 1);
    }

    [TestMethod]
    public void Validate_Throws_ProviderNotReady_WhenAzureOpenAiSectionMissing()
    {
        var validator = CreateValidator(new Dictionary<string, string?>());

        Action act = () => validator.Validate(new ChatTarget("gpt-4.1", "azure-openai-chat", RuntimeConfigJson: null));
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ProviderNotReady
                         && ex.ProviderSection == "AzureOpenAI"
                         && ex.Blockers.Count >= 2);
    }

    [TestMethod]
    public void Validate_AcceptsAnthropic_WhenApiKeyOrAuthTokenPresent()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = "sk-ant-xxx"
        });

        Action act = () => validator.Validate(new ChatTarget("claude-sonnet-4-5", "anthropic", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_Throws_ProviderNotReady_WhenAnthropicMissingAllAlternatives()
    {
        var validator = CreateValidator(new Dictionary<string, string?>());

        Action act = () => validator.Validate(new ChatTarget("claude-sonnet-4-5", "anthropic", RuntimeConfigJson: null));
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ProviderNotReady
                         && ex.ProviderSection == "Anthropic");
    }

    [TestMethod]
    public void Validate_Throws_ProviderNotReady_ForUnsupportedProvider()
    {
        var validator = CreateValidator(new Dictionary<string, string?>());

        Action act = () => validator.Validate(new ChatTarget("model-x", "some-other-vendor", RuntimeConfigJson: null));
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ProviderNotReady
                         && ex.ModelId == "model-x");
    }

    [TestMethod]
    public void Validate_AcceptsGoogleGeminiChat_WhenRequiredFieldsConfigured()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["GoogleGeminiApi:ApiKey"] = "gemini-key"
        });

        Action act = () => validator.Validate(new ChatTarget("gemini-2.5-pro", "google-gemini-chat", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_Throws_ProviderNotReady_WhenGoogleGeminiChatMissingApiKey()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
        });

        Action act = () => validator.Validate(new ChatTarget("gemini-2.5-pro", "google-gemini-chat", RuntimeConfigJson: null));
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ProviderNotReady
                         && ex.ProviderSection == "GoogleGeminiApi");
    }

    [TestMethod]
    public void Validate_AcceptsHfInferenceChat_WhenTokenConfigured()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["HuggingFace:Token"] = "hf_xxx"
        });

        Action act = () => validator.Validate(new ChatTarget("meta-llama/llama-4-scout", "hf-inference-chat", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_AcceptsOpenRouterChat_WhenApiKeyConfigured()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["OpenRouter:ApiKey"] = "or_xxx"
        });

        Action act = () => validator.Validate(new ChatTarget("openai/gpt-4o-mini", "openrouter-chat", RuntimeConfigJson: null));
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_Throws_ModelNotReady_WhenLlamaCppHasNoRuntimeConfigJson()
    {
        var validator = CreateValidator(new Dictionary<string, string?>());

        Action act = () => validator.Validate(new ChatTarget("qwen-local", "llama-cpp", RuntimeConfigJson: null));
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModelNotReady
                         && ex.ModelId == "qwen-local");
    }

    [TestMethod]
    public void Validate_Throws_ModelNotReady_WhenLlamaCppRuntimeConfigJsonIsMalformed()
    {
        var validator = CreateValidator(new Dictionary<string, string?>());

        var target = new ChatTarget("qwen-local", "llama-cpp", RuntimeConfigJson: "{ not valid json ::");
        Action act = () => validator.Validate(target);
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModelNotReady
                         && ex.ModelId == "qwen-local");
    }

    [TestMethod]
    public void Validate_Throws_RuntimeNotReady_WhenRuntimeProfileResolutionFails()
    {
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();
        runtimeProfileResolver
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("profile missing"));

        var validator = new ChatTargetValidator(
            BuildConfiguration(new Dictionary<string, string?>()),
            runtimeProfileResolver.Object);

        var runtimeJson = """
        {
            "routerModelId": "qwen-model",
            "runtimeProfileId": "qwen3_5",
            "loadParams": { "model": "qwen-model" }
        }
        """;

        var target = new ChatTarget("qwen-local", "llama-cpp", runtimeJson);
        Action act = () => validator.Validate(target);
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.RuntimeNotReady
                         && ex.ModelId == "qwen-local");
    }

    /// <summary>
    /// Phase H.2 (R-12.5): the chat-dispatch validator must NOT inspect live
    /// llama runtime state. If a load is in flight for the alias, the
    /// validator must still succeed provided the static inputs (provider,
    /// RuntimeConfigJson, runtime profile) are well-formed — the runtime
    /// readiness snapshot lives on <c>IRoutingReadinessService.ProbeChatTargetAsync</c>,
    /// not on this path.
    ///
    /// Structurally this is already true: the validator's constructor accepts
    /// only <c>IConfiguration</c> + <c>IRuntimeProfileResolver</c>, so it
    /// cannot check load state even if it wanted to. This test pins the
    /// behavior against a future refactor that might add a coordinator
    /// dependency and inadvertently start blocking chat on load state.
    /// </summary>
    [TestMethod]
    public void Validate_DoesNotCheckLlamaLoadState_PerR12p5()
    {
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();
        runtimeProfileResolver
            .Setup(r => r.ResolveAsync("concurrency-profile", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuntimeProfileData(
                "concurrency-profile",
                CombineSystemAndDeveloperMessages: true,
                ThoughtBlockPattern: null,
                SamplingParameters: new Dictionary<string, SamplingParameterDefinition>(),
                ThinkingControl: new ThinkingControl("disabled", new Dictionary<string, IReadOnlyList<ThinkingAction>>())));

        var validator = new ChatTargetValidator(
            BuildConfiguration(new Dictionary<string, string?>()),
            runtimeProfileResolver.Object);

        var runtimeJson = """
        {
            "routerModelId": "qwen-mid-load",
            "runtimeProfileId": "concurrency-profile",
            "loadParams": { "model": "qwen-mid-load" }
        }
        """;

        Action act = () => validator.Validate(new ChatTarget("qwen-local", "llama-cpp", runtimeJson));
        act.Should().NotThrow(
            "R-12.5: while the alias is mid-load the chat dispatch path validates static inputs only; " +
            "live load state is not inspected here (that's the readiness snapshot's job).");
    }

    [TestMethod]
    public void Validate_AcceptsLlamaCpp_WhenRuntimeProfileResolves()
    {
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();
        runtimeProfileResolver
            .Setup(r => r.ResolveAsync("qwen3_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuntimeProfileData(
                "qwen3_5",
                CombineSystemAndDeveloperMessages: true,
                ThoughtBlockPattern: @"<think>[\s\S]*?</think>",
                SamplingParameters: new Dictionary<string, SamplingParameterDefinition>(),
                ThinkingControl: new ThinkingControl("enabled", new Dictionary<string, IReadOnlyList<ThinkingAction>>())));

        var validator = new ChatTargetValidator(
            BuildConfiguration(new Dictionary<string, string?>()),
            runtimeProfileResolver.Object);

        var runtimeJson = """
        {
            "routerModelId": "qwen-model",
            "runtimeProfileId": "qwen3_5",
            "loadParams": { "model": "qwen-model" }
        }
        """;

        Action act = () => validator.Validate(new ChatTarget("qwen-local", "llama-cpp", runtimeJson));
        act.Should().NotThrow();
    }

    private static ChatTargetValidator CreateValidator(Dictionary<string, string?> settings)
    {
        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();
        return new ChatTargetValidator(BuildConfiguration(settings), runtimeProfileResolver.Object);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

