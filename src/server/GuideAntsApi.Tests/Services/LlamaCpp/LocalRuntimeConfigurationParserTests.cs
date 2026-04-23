using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LocalRuntimeConfigurationParserTests
{
    [TestMethod]
    public void Parse_Throws_WhenMissingRequiredField()
    {
        const string json = """{"routerModelId":"qwen-router","runtimeProfileId":"qwen3_5"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing required field(s): resourceGroupKey*");
    }

    [TestMethod]
    public void Parse_Throws_WhenUnknownFieldIsPresent()
    {
        const string json = """{"routerModelId":"qwen-router","resourceGroupKey":"rg","runtimeProfileId":"qwen3_5","extraField":true}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unsupported field(s): extraField*");
    }

    [TestMethod]
    public void Parse_Throws_WhenRouterModelIncludesGgufSuffix()
    {
        const string json = """{"routerModelId":"qwen-router.gguf","resourceGroupKey":"rg","runtimeProfileId":"qwen3_5"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*.gguf*");
    }

    [TestMethod]
    public void Parse_Throws_WhenLoadParamsIsStringifiedJson()
    {
        const string json = """{"routerModelId":"qwen-router","resourceGroupKey":"rg","runtimeProfileId":"qwen3_5","loadParams":"{\"model\":\"qwen-router\"}"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'loadParams' must be a JSON object*");
    }

    [TestMethod]
    public void Parse_Throws_WhenParallelToolCallsIsNotBoolean()
    {
        const string json = """{"routerModelId":"qwen-router","resourceGroupKey":"rg","runtimeProfileId":"qwen3_5","parallelToolCalls":"yes"}""";

        Action act = () => LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'parallelToolCalls' must be a boolean*");
    }

    [TestMethod]
    public void Parse_AcceptsCanonicalShape()
    {
        const string json = """{"routerModelId":"qwen-router","resourceGroupKey":"rg","runtimeProfileId":"qwen3_5","loadParams":{"model":"qwen-router"},"parallelToolCalls":true}""";

        var parsed = LocalRuntimeConfigurationParser.Parse("qwen3.5-27b", json);

        parsed.RouterModelId.Should().Be("qwen-router");
        parsed.ResourceGroupKey.Should().Be("rg");
        parsed.RuntimeProfileId.Should().Be("qwen3_5");
        parsed.LoadParams.Should().NotBeNull();
        parsed.LoadParams!["model"]!.GetValue<string>().Should().Be("qwen-router");
        parsed.ParallelToolCalls.Should().BeTrue();
    }
}
