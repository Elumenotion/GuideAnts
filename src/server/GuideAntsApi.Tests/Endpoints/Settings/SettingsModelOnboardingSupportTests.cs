using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Tests.Endpoints.Settings;

[TestClass]
public sealed class SettingsModelOnboardingSupportTests
{
    private static AddModelRequest CreateRequest(string provider, string? runtimeConfigJson)
    {
        var providerConfig = new JsonObject { ["samplingParametersJson"] = "{}" };
        if (runtimeConfigJson is not null)
        {
            providerConfig["runtimeConfigJson"] = runtimeConfigJson;
        }

        return new AddModelRequest(
            Provider: provider,
            Catalog: new AddModelCatalogDto("vllm-qwen", "Qwen via vLLM", null, 0, true),
            ProviderConfig: providerConfig,
            Install: null);
    }

    [TestMethod]
    public void BuildCloudModelCreateRequest_CarriesRuntimeConfigJson_ForOpenAiCompatible()
    {
        var runtimeConfig = """{"baseUrl":"http://localhost:8000/v1","apiKey":"row-key"}""";

        var request = SettingsModelOnboardingSupport.BuildCloudModelCreateRequest(CreateRequest("openai-compatible", runtimeConfig));

        request.RuntimeConfigJson.Should().Be(runtimeConfig);
        request.Provider.Should().Be("openai-compatible");
        request.ModelId.Should().Be("vllm-qwen");
    }

    [TestMethod]
    public void BuildCloudModelCreateRequest_NormalizesWhitespace_InRuntimeConfigJson()
    {
        var runtimeConfig = "  " + """{"baseUrl":"http://localhost:8000/v1"}""" + "  ";

        var request = SettingsModelOnboardingSupport.BuildCloudModelCreateRequest(CreateRequest("openai-compatible", runtimeConfig));

        request.RuntimeConfigJson.Should().Be("""{"baseUrl":"http://localhost:8000/v1"}""");
    }

    [TestMethod]
    public void BuildCloudModelCreateRequest_RuntimeConfigJsonIsNull_WhenNotProvided()
    {
        var request = SettingsModelOnboardingSupport.BuildCloudModelCreateRequest(CreateRequest("openai-compatible", null));

        request.RuntimeConfigJson.Should().BeNull();
    }

    [TestMethod]
    public void BuildCloudModelCreateRequest_RuntimeConfigJsonIsNull_ForOtherProviders()
    {
        var runtimeConfig = """{"baseUrl":"http://localhost:8000/v1"}""";
        var request = SettingsModelOnboardingSupport.BuildCloudModelCreateRequest(CreateRequest("openrouter-chat", runtimeConfig));

        // Only openai-compatible rows carry row-owned connection details; other
        // providers keep null as before.
        request.RuntimeConfigJson.Should().BeNull();
    }
}
