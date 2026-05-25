using FluentAssertions;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;
using Moq;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ChatModelResolverTests
{
    private static IConfiguration BuildConfig(IReadOnlyDictionary<string, string?> pairs)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(pairs!)
            .Build();
    }

    private static ChatModelResolver BuildResolver(IConfiguration configuration)
    {
        var targetResolver = new Mock<IChatTargetResolver>();
        targetResolver
            .Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns((string modelId) => new ChatTarget(
                modelId,
                modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase) ? "google-gemini-chat" : "openai-responses",
                null));
        return new ChatModelResolver(configuration, targetResolver.Object);
    }

    [TestMethod]
    public void Resolve_OverrideAll_UsesDefault_IgnoresEntity_AndSetsGlobalAuthority()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "true",
            ["ChatDefaults:DefaultModelId"] = "global-default",
            ["ChatDefaults:Temperature"] = "0.42",
            ["ChatDefaults:TopP"] = "0.9",
            ["ChatDefaults:ReasoningEffort"] = "high",
            ["ChatDefaults:SamplingParametersJson"] = """{"min_p":0.05}"""
        });
        var resolver = BuildResolver(config);

        var result = resolver.Resolve("entity-model");

        result.ModelId.Should().Be("global-default");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.OverriddenToDefault);
        result.ExecutionPolicy.Authority.Should().Be(ParameterAuthority.GlobalOverride);
        result.ExecutionPolicy.Parameters["temperature"].GetDouble().Should().BeApproximately(0.42, 0.001);
        result.ExecutionPolicy.Parameters["top_p"].GetDouble().Should().BeApproximately(0.9, 0.001);
        result.ExecutionPolicy.Parameters["reasoning_effort"].GetString().Should().Be("high");
        result.ExecutionPolicy.Parameters["min_p"].GetDouble().Should().BeApproximately(0.05, 0.001);
    }

    [TestMethod]
    public void Resolve_EntityModel_WhenOverrideOff_IsAssistantDefinition_WithEmptyParameterBag()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "false",
            ["ChatDefaults:DefaultModelId"] = "ignored-when-entity-set",
        });
        var resolver = BuildResolver(config);

        var result = resolver.Resolve("my-model");

        result.ModelId.Should().Be("my-model");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.Direct);
        result.ExecutionPolicy.Authority.Should().Be(ParameterAuthority.AssistantDefinition);
        result.ExecutionPolicy.Parameters.Should().BeEmpty();
    }

    [TestMethod]
    public void Resolve_BlankEntity_WithDefaultConfigured_IsAssistantDefinition_WithDefaultParameterBag()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "false",
            ["ChatDefaults:DefaultModelId"] = "def",
            ["ChatDefaults:Temperature"] = "0.7",
        });
        var resolver = BuildResolver(config);

        var result = resolver.Resolve("  ");

        result.ModelId.Should().Be("def");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.DefaultedTo);
        result.ExecutionPolicy.Authority.Should().Be(ParameterAuthority.AssistantDefinition);
        result.ExecutionPolicy.Parameters["temperature"].GetDouble().Should().BeApproximately(0.7, 0.001);
    }

    [TestMethod]
    public void Resolve_OverrideAll_WithoutDefault_ThrowsRoutingException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "true",
            ["ChatDefaults:DefaultModelId"] = "",
        });
        var resolver = BuildResolver(config);

        var act = () => resolver.Resolve(null);
        act.Should().Throw<RoutingException>();
    }

    [TestMethod]
    public void Resolve_NoEntity_NoDefault_ThrowsRoutingException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "false",
            ["ChatDefaults:DefaultModelId"] = "",
        });
        var resolver = BuildResolver(config);

        var act = () => resolver.Resolve(null);
        act.Should().Throw<RoutingException>();
    }
}
