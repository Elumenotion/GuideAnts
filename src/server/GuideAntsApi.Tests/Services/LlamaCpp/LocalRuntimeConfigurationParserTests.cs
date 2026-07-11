using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LocalRuntimeConfigurationParserTests
{
    [TestMethod]
    public void Parse_Throws_WhenMissingRequiredField()
    {
        const string json = """{"routerModelId":"qwen-router"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing required field(s): runtimeProfileId*");
    }

    [TestMethod]
    public void Parse_Throws_WhenRouterModelIncludesGgufSuffix()
    {
        const string json = """{"routerModelId":"qwen-router.gguf","runtimeProfileId":"qwen3_5"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*.gguf*");
    }

    [TestMethod]
    public void Parse_AcceptsCanonicalShape()
    {
        const string json = """{"routerModelId":"qwen-router","runtimeProfileId":"qwen3_5"}""";

        var parsed = LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        parsed.RouterModelId.Should().Be("qwen-router");
        parsed.RuntimeProfileId.Should().Be("qwen3_5");
        LocalRuntimeConfigurationParser.SerializeCanonical(parsed)
            .Should().Be("""{"routerModelId":"qwen-router","runtimeProfileId":"qwen3_5"}""");
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
        legacy.LoadParams.Should().NotBeNull();
        legacy.LoadParams!["foo"]!.GetValue<string>().Should().Be("bar");
        legacy.ParallelToolCalls.Should().BeTrue();
        legacy.RouterContextSize.Should().Be(8192);
        legacy.RouterCacheRamMib.Should().Be(1024);
    }
}
