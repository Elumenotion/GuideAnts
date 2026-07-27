using FluentAssertions;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ChatModelResolverTests
{
    private sealed class TestChatDefaultsStore(ChatDefaultsSnapshot snapshot) : IChatDefaultsStore
    {
        public ChatDefaultsSnapshot Current { get; } = snapshot;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ChatModelResolver BuildResolver(ChatDefaultsSnapshot snapshot)
    {
        var targetResolver = new Mock<IChatTargetResolver>();
        targetResolver
            .Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns((string modelId) => new ChatTarget(
                modelId,
                modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase) ? "google-gemini-chat" : "openai-responses",
                null));
        return new ChatModelResolver(new TestChatDefaultsStore(snapshot), targetResolver.Object);
    }

    [TestMethod]
    public void Resolve_OverrideAll_UsesDefault_IgnoresEntity_AndSetsGlobalAuthority()
    {
        var snapshot = new ChatDefaultsSnapshot(
            DefaultModelId: "global-default",
            OverrideAllChatModels: true,
            Temperature: 0.42,
            TopP: 0.9,
            ReasoningEffort: "high",
            SamplingParametersJson: """{"min_p":0.05}""");
        var resolver = BuildResolver(snapshot);

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
        var snapshot = new ChatDefaultsSnapshot(
            DefaultModelId: "ignored-when-entity-set",
            OverrideAllChatModels: false,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null);
        var resolver = BuildResolver(snapshot);

        var result = resolver.Resolve("my-model");

        result.ModelId.Should().Be("my-model");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.Direct);
        result.ExecutionPolicy.Authority.Should().Be(ParameterAuthority.AssistantDefinition);
        result.ExecutionPolicy.Parameters.Should().BeEmpty();
    }

    [TestMethod]
    public void Resolve_BlankEntity_WithDefaultConfigured_IsAssistantDefinition_WithDefaultParameterBag()
    {
        var snapshot = new ChatDefaultsSnapshot(
            DefaultModelId: "def",
            OverrideAllChatModels: false,
            Temperature: 0.7,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null);
        var resolver = BuildResolver(snapshot);

        var result = resolver.Resolve("  ");

        result.ModelId.Should().Be("def");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.DefaultedTo);
        result.ExecutionPolicy.Authority.Should().Be(ParameterAuthority.AssistantDefinition);
        result.ExecutionPolicy.Parameters["temperature"].GetDouble().Should().BeApproximately(0.7, 0.001);
    }

    [TestMethod]
    public void Resolve_OverrideAll_WithoutDefault_ThrowsRoutingException()
    {
        var snapshot = new ChatDefaultsSnapshot(
            DefaultModelId: null,
            OverrideAllChatModels: true,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null);
        var resolver = BuildResolver(snapshot);

        var act = () => resolver.Resolve(null);
        act.Should().Throw<RoutingException>();
    }

    [TestMethod]
    public void Resolve_OverrideAll_IgnoresClearedTemperatureAndTopP()
    {
        var snapshot = new ChatDefaultsSnapshot(
            DefaultModelId: "gpt-5.5",
            OverrideAllChatModels: true,
            Temperature: null,
            TopP: null,
            ReasoningEffort: "low",
            SamplingParametersJson: null);
        var resolver = BuildResolver(snapshot);

        var result = resolver.Resolve("entity-model");

        result.ExecutionPolicy.Parameters.Should().NotContainKey("temperature");
        result.ExecutionPolicy.Parameters.Should().NotContainKey("top_p");
        result.ExecutionPolicy.Parameters["reasoning_effort"].GetString().Should().Be("low");
    }

    [TestMethod]
    public void Resolve_NoEntity_NoDefault_ThrowsRoutingException()
    {
        var snapshot = ChatDefaultsSnapshot.Empty;
        var resolver = BuildResolver(snapshot);

        var act = () => resolver.Resolve(null);
        act.Should().Throw<RoutingException>();
    }
}
