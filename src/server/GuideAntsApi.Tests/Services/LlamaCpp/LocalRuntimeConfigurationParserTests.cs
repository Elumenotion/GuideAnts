using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LocalRuntimeConfigurationParserTests
{
    [TestMethod]
    public void Parse_Throws_WhenMissingRequiredField()
    {
        const string json = """{}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing required field(s): routerModelId*");
    }

    [TestMethod]
    public void Parse_Throws_WhenRouterModelIncludesGgufSuffix()
    {
        const string json = """{"routerModelId":"qwen-router.gguf"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*.gguf*");
    }

    [TestMethod]
    public void Parse_AcceptsCanonicalShape()
    {
        const string json = """{"routerModelId":"qwen-router"}""";

        var parsed = LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        parsed.RouterModelId.Should().Be("qwen-router");
        LocalRuntimeConfigurationParser.SerializeCanonical(parsed)
            .Should().Be("""{"routerModelId":"qwen-router"}""");
    }

    [TestMethod]
    public void MigrationReader_ReadsLegacyFields_WithoutFinalParser()
    {
        const string json = """
            {
              "routerModelId":"qwen-router",
              "runtimeProfileId":"qwen3_5",
              "loadParams":{"model":"qwen-router","foo":"bar"},
              "parallelToolCalls":true,
              "routerContextSize":8192,
              "routerCacheRamMib":1024
            }
            """;

        var legacy = LocalRuntimeConfigurationMigrationReader.ReadLegacy("qwen3.5-27b", json);

        legacy.RouterModelId.Should().Be("qwen-router");
        legacy.RuntimeProfileId.Should().Be("qwen3_5");
        legacy.LoadParams.Should().NotBeNull();
        legacy.LoadParams!["foo"]!.GetValue<string>().Should().Be("bar");
        legacy.ParallelToolCalls.Should().BeTrue();
        legacy.RouterContextSize.Should().Be(8192);
        legacy.RouterCacheRamMib.Should().Be(1024);
    }

    [TestMethod]
    public void Parse_AcceptsRowOwnedStackAndRoundTrips()
    {
        const string json = """{"routerModelId":"Qwen3.6-27B-MTP-GGUF","stackBaseUrl":"http://192.0.2.1:8112","stackApiKey":"secret"}""";

        var parsed = LocalRuntimeConfigurationParser.Parse("qwen3.6-27b-max", json);

        parsed.RouterModelId.Should().Be("Qwen3.6-27B-MTP-GGUF");
        parsed.StackBaseUrl.Should().Be("http://192.0.2.1:8112");
        parsed.StackApiKey.Should().Be("secret");
        parsed.UsesGlobalStack.Should().BeFalse();
        LocalRuntimeConfigurationParser.SerializeCanonical(parsed)
            .Should().Be(json);
    }

    [TestMethod]
    public void Parse_LegacySingleFieldShapeTargetsGlobalStack()
    {
        const string json = """{"routerModelId":"qwen-router"}""";

        var parsed = LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        parsed.StackBaseUrl.Should().BeEmpty();
        parsed.StackApiKey.Should().BeEmpty();
        parsed.UsesGlobalStack.Should().BeTrue();
        LocalRuntimeConfigurationParser.SerializeCanonical(parsed).Should().Be(json);
    }

    [TestMethod]
    public void Parse_Throws_WhenStackBaseUrlHasTrailingSlash()
    {
        const string json = """{"routerModelId":"qwen-router","stackBaseUrl":"http://192.0.2.1:8112/"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("m", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*trailing '/'*");
    }

    [TestMethod]
    public void Parse_Throws_WhenStackBaseUrlIsRelative()
    {
        const string json = """{"routerModelId":"qwen-router","stackBaseUrl":"guideants-ai:80"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("m", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }

    [TestMethod]
    public void Parse_Throws_WhenStackBaseUrlIsNonHttp()
    {
        const string json = """{"routerModelId":"qwen-router","stackBaseUrl":"file:///models"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("m", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }
}
