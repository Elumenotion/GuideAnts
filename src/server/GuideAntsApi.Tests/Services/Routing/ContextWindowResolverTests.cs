using FluentAssertions;
using GuideAntsApi.Services.Routing;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ContextWindowResolverTests
{
    private static IContextWindowResolver Build(
        int? catalogWindow,
        int? catalogMaxOutput = null,
        int? learned = null)
    {
        var targets = new Mock<IChatTargetResolver>();
        targets.Setup(t => t.Resolve(It.IsAny<string>()))
            .Returns(new ChatTarget(
                "m", "openai-chat", null, null, catalogWindow, catalogMaxOutput));

        var cache = new Mock<ILearnedContextWindowCache>();
        cache.Setup(c => c.Get(It.IsAny<string>())).Returns(learned);

        return new ContextWindowResolver(targets.Object, cache.Object);
    }

    private static IContextWindowResolver BuildUnknownModel(int? learned = null)
    {
        var targets = new Mock<IChatTargetResolver>();
        targets.Setup(t => t.Resolve(It.IsAny<string?>()))
            .Throws(RoutingException.ModelNotReady("m", "Model not found in the catalog."));

        var cache = new Mock<ILearnedContextWindowCache>();
        cache.Setup(c => c.Get(It.IsAny<string>())).Returns(learned);

        return new ContextWindowResolver(targets.Object, cache.Object);
    }

    [TestMethod]
    public void LiveRuntimeValue_WinsOverCatalog()
    {
        var result = Build(catalogWindow: 128_000).Resolve("m", liveRuntimeContextSize: 8_192);

        result.ContextWindowTokens.Should().Be(8_192);
        result.Source.Should().Be(ContextWindowSource.LiveRuntime);
    }

    [TestMethod]
    public void Catalog_WinsOverLearned()
    {
        var result = Build(catalogWindow: 128_000, learned: 8_192)
            .Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().Be(128_000);
        result.Source.Should().Be(ContextWindowSource.Catalog);
    }

    [TestMethod]
    public void Learned_UsedWhenCatalogIsNull()
    {
        var result = Build(catalogWindow: null, learned: 8_192)
            .Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().Be(8_192);
        result.Source.Should().Be(ContextWindowSource.Learned);
    }

    [TestMethod]
    public void Unknown_WhenNoSourceHasAValue()
    {
        var result = Build(catalogWindow: null).Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().BeNull();
        result.MaxOutputTokens.Should().BeNull();
        result.Source.Should().Be(ContextWindowSource.Unknown);
    }

    [TestMethod]
    public void MaxOutputTokens_AlwaysComesFromCatalog()
    {
        // Only the catalog knows the output cap; a runtime window says nothing about it.
        var result = Build(catalogWindow: 128_000, catalogMaxOutput: 64_000)
            .Resolve("m", liveRuntimeContextSize: 8_192);

        result.ContextWindowTokens.Should().Be(8_192);
        result.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void NonPositiveLiveValue_IsIgnored()
    {
        var result = Build(catalogWindow: 128_000).Resolve("m", liveRuntimeContextSize: 0);

        result.ContextWindowTokens.Should().Be(128_000);
        result.Source.Should().Be(ContextWindowSource.Catalog);
    }

    [TestMethod]
    public void UnknownModel_WithLearnedValue_ReturnsLearned()
    {
        var result = BuildUnknownModel(learned: 8_192).Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().Be(8_192);
        result.MaxOutputTokens.Should().BeNull();
        result.Source.Should().Be(ContextWindowSource.Learned);
    }

    [TestMethod]
    public void UnknownModel_WithNothingLearned_IsUnknownAndDoesNotThrow()
    {
        var act = () => BuildUnknownModel().Resolve("m", liveRuntimeContextSize: null);

        var result = act.Should().NotThrow().Subject;
        result.ContextWindowTokens.Should().BeNull();
        result.MaxOutputTokens.Should().BeNull();
        result.Source.Should().Be(ContextWindowSource.Unknown);
    }

    [TestMethod]
    public void UnknownModel_WithLiveRuntimeValue_ReturnsLiveRuntime()
    {
        var result = BuildUnknownModel(learned: 4_096).Resolve("m", liveRuntimeContextSize: 8_192);

        result.ContextWindowTokens.Should().Be(8_192);
        result.Source.Should().Be(ContextWindowSource.LiveRuntime);
    }
}
