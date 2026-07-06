using System.Net;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class SettingsSupportTests
{
    [TestMethod]
    public void MapChatDefaults_RoundTripsThroughBuildPayload()
    {
        var request = new UpdateChatDefaultsRequest(
            RowVersion: "3",
            DefaultModelId: "gpt-test",
            OverrideAllChatModels: true,
            Temperature: 0.7,
            TopP: 0.9,
            ReasoningEffort: "medium",
            SamplingParametersJson: "{\"k\":1}");

        var payload = SettingsChatDefaultsMapper.BuildChatDefaultsPayload(request);
        var section = new SettingsSectionDto(
            SectionName: "ChatDefaults",
            SchemaVersion: 1,
            RowVersion: "3",
            UpdatedUtc: DateTime.UtcNow,
            Payload: payload,
            SecretHasValue: new Dictionary<string, bool>());
        var dto = SettingsChatDefaultsMapper.MapChatDefaults(section);

        dto.DefaultModelId.Should().Be("gpt-test");
        dto.OverrideAllChatModels.Should().BeTrue();
        dto.Temperature.Should().Be(0.7);
        dto.TopP.Should().Be(0.9);
        dto.ReasoningEffort.Should().Be("medium");
        dto.SamplingParametersJson.Should().Be("{\"k\":1}");
        dto.RowVersion.Should().Be("3");
    }

    [TestMethod]
    public async Task ValidateDownloadPayload_ImageGeneration_RejectsMissingBundleId()
    {
        using var doc = JsonDocument.Parse("""{"diffusion_repo":"r","diffusion_file":"f.gguf"}""");
        var result = ServiceLocalModelDownloadValidator.ValidateDownloadPayload("ImageGeneration", doc.RootElement);

        result.Should().NotBeNull();
        (await ExecuteResultAsync(result!)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [TestMethod]
    public async Task ValidateDownloadPayload_ImageGeneration_RejectsGlobInFilename()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "bundle_id": "b1",
              "diffusion_repo": "org/diff",
              "diffusion_file": "*.gguf",
              "vae_repo": "org/vae",
              "vae_file": "vae.safetensors",
              "text_encoder_repo": "org/te",
              "text_encoder_file": "te.safetensors"
            }
            """);

        var result = ServiceLocalModelDownloadValidator.ValidateDownloadPayload("ImageGeneration", doc.RootElement);

        result.Should().NotBeNull();
        (await ExecuteResultAsync(result!)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [TestMethod]
    public async Task ValidateDownloadPayload_NonImageGeneration_RequiresModelId()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = ServiceLocalModelDownloadValidator.ValidateDownloadPayload("Embeddings", doc.RootElement);

        result.Should().NotBeNull();
        (await ExecuteResultAsync(result!)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [TestMethod]
    public async Task ValidateCatalogMembership_RejectsUnknownModelId()
    {
        var catalogIds = new HashSet<string>(StringComparer.Ordinal) { "qwen3_asr_0_6b" };
        var result = ServiceLocalModelDownloadValidator.ValidateCatalogMembership("whisper-large-v3", catalogIds);

        result.Should().NotBeNull();
        (await ExecuteResultAsync(result!)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [TestMethod]
    public void ValidateCatalogMembership_AcceptsKnownModelId()
    {
        var catalogIds = new HashSet<string>(StringComparer.Ordinal) { "qwen3_asr_0_6b" };
        var result = ServiceLocalModelDownloadValidator.ValidateCatalogMembership("qwen3_asr_0_6b", catalogIds);

        result.Should().BeNull();
    }

    [TestMethod]
    public void TryParseCatalogIds_ReadsEntryIds()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "version": "1",
              "entries": [
                { "id": "qwen3_asr_0_6b", "displayName": "Qwen3 ASR" },
                { "id": "chatterbox", "displayName": "Chatterbox" }
              ]
            }
            """);

        ServiceLocalModelCatalogSupport.TryParseCatalogIds(doc.RootElement, out var ids, out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
        ids.Should().BeEquivalentTo(["qwen3_asr_0_6b", "chatterbox"]);
    }

    [TestMethod]
    public async Task GetCatalogIdsAsync_FetchesFreshCatalogEachCall()
    {
        var handler = new StaticCatalogHandler(
            """
            {
              "version": "1",
              "entries": [{ "id": "qwen3_asr_0_6b" }]
            }
            """);
        var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://guideants-ai:80",
            })
            .Build();

        var first = await ServiceLocalModelCatalogSupport.GetCatalogIdsAsync(
            "SpeechTranscription",
            configuration,
            httpClient,
            CancellationToken.None);
        var second = await ServiceLocalModelCatalogSupport.GetCatalogIdsAsync(
            "SpeechTranscription",
            configuration,
            httpClient,
            CancellationToken.None);

        first.Error.Should().BeNull();
        first.Ids.Should().ContainSingle().Which.Should().Be("qwen3_asr_0_6b");
        second.Error.Should().BeNull();
        second.Ids.Should().BeEquivalentTo(first.Ids);
        handler.RequestCount.Should().Be(2);
    }

    [TestMethod]
    public void InferChatTargetReferenceKind_OverrideAll_ReturnsOverriddenToDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatDefaults:DefaultModelId"] = "default-model",
                ["ChatDefaults:OverrideAllChatModels"] = "true",
            })
            .Build();

        SettingsRoutingProbeSupport.InferChatTargetReferenceKind("default-model", configuration, anyAssistantsWithoutModel: false)
            .Should().Be("overriddenToDefault");
    }

    [TestMethod]
    public void InferChatTargetReferenceKind_DefaultedAssistants_ReturnsDefaultedTo()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatDefaults:DefaultModelId"] = "default-model",
                ["ChatDefaults:OverrideAllChatModels"] = "false",
            })
            .Build();

        SettingsRoutingProbeSupport.InferChatTargetReferenceKind("default-model", configuration, anyAssistantsWithoutModel: true)
            .Should().Be("defaultedTo");
    }

    [TestMethod]
    public void InferChatTargetReferenceKind_DirectReference_ReturnsDirect()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatDefaults:DefaultModelId"] = "default-model",
                ["ChatDefaults:OverrideAllChatModels"] = "false",
            })
            .Build();

        SettingsRoutingProbeSupport.InferChatTargetReferenceKind("other-model", configuration, anyAssistantsWithoutModel: true)
            .Should().Be("direct");
    }

    private static async Task<int> ExecuteResultAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;

        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private sealed class StaticCatalogHandler(string catalogJson) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(catalogJson, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
