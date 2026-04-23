using FluentAssertions;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;

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

    [TestMethod]
    public void Resolve_OverrideAll_UsesDefault_IgnoresEntity_AndSetsReferenceKind()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "true",
            ["ChatDefaults:DefaultModelId"] = "global-default",
            ["ChatDefaults:Temperature"] = "0.42",
            ["ChatDefaults:TopP"] = "0.9",
            ["ChatDefaults:ReasoningEffort"] = "high",
        });
        var resolver = new ChatModelResolver(config);

        var result = resolver.Resolve("entity-model");

        result.ModelId.Should().Be("global-default");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.OverriddenToDefault);
        result.OverrideTemperature.Should().BeApproximately(0.42f, 0.001f);
        result.OverrideTopP.Should().BeApproximately(0.9f, 0.001f);
        result.OverrideReasoningEffort.Should().Be("high");
    }

    [TestMethod]
    public void Resolve_EntityModel_WhenOverrideOff_IsDirect_WithoutOverrides()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "false",
            ["ChatDefaults:DefaultModelId"] = "ignored-when-entity-set",
        });
        var resolver = new ChatModelResolver(config);

        var result = resolver.Resolve("my-model");

        result.ModelId.Should().Be("my-model");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.Direct);
        result.OverrideTemperature.Should().BeNull();
        result.OverrideTopP.Should().BeNull();
        result.OverrideReasoningEffort.Should().BeNull();
    }

    [TestMethod]
    public void Resolve_BlankEntity_WithDefaultConfigured_IsDefaultedTo_WithOverrides()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "false",
            ["ChatDefaults:DefaultModelId"] = "def",
            ["ChatDefaults:Temperature"] = "0.7",
        });
        var resolver = new ChatModelResolver(config);

        var result = resolver.Resolve("  ");

        result.ModelId.Should().Be("def");
        result.ReferenceKind.Should().Be(ChatModelReferenceKind.DefaultedTo);
        result.OverrideTemperature.Should().BeApproximately(0.7f, 0.001f);
    }

    [TestMethod]
    public void Resolve_OverrideAll_WithoutDefault_ThrowsRoutingException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ChatDefaults:OverrideAllChatModels"] = "true",
            ["ChatDefaults:DefaultModelId"] = "",
        });
        var resolver = new ChatModelResolver(config);

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
        var resolver = new ChatModelResolver(config);

        var act = () => resolver.Resolve(null);
        act.Should().Throw<RoutingException>();
    }
}
