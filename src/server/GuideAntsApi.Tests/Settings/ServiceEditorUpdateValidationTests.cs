using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ServiceEditorUpdateValidationTests
{
    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_RejectsUnknownField()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "Embeddings",
        [
            new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", null, null, true, true)
        ]);

        var act = async () => await service.UpdateServiceProviderFieldsAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["TimeoutSeconds"] = "30",
                    ["NotARealField"] = "x"
                })),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("Unknown field", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_RejectsInvalidServiceField()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "Embeddings",
        [
            new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", null, null, true, true)
        ]);

        var act = async () => await service.UpdateServiceProviderFieldsAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsLocalEmbHttp,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["TimeoutSeconds"] = "not-a-number"
                })),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("whole number", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_PersistsModelIdIntoServiceModes_ForCloudProvidersThatNeedIt()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "Embeddings",
        [
            new ServiceMode("huggingface", "HuggingFace", null, null, true, true)
        ]);

        await service.UpdateServiceProviderFieldsAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsHuggingFaceInference,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["ModelId"] = "Qwen/Qwen3-Embedding-0.6B"
                })),
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsHuggingFaceInference,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("Embeddings", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "HuggingFace", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "Qwen/Qwen3-Embedding-0.6B", StringComparison.Ordinal));

        var state = await service.GetServiceEditorStateAsync("Embeddings", CancellationToken.None);
        var provider = state.Providers.Single(p => p.ProviderId == ServiceProviderIds.EmbeddingsHuggingFaceInference);
        provider.Fields["ModelId"].Value.Should().Be("Qwen/Qwen3-Embedding-0.6B");
        provider.Fields["ModelId"].HasValue.Should().BeTrue();
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_PersistsModelIdIntoServiceModes_ForOpenAiEmbeddings()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "Embeddings",
        [
            new ServiceMode("openai", "OpenAI", null, null, true, true)
        ]);

        await service.UpdateServiceProviderFieldsAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsOpenAiEmbedding,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["ModelId"] = "text-embedding-3-small"
                })),
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsOpenAiEmbedding,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("Embeddings", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "OpenAI", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "text-embedding-3-small", StringComparison.Ordinal));

        var state = await service.GetServiceEditorStateAsync("Embeddings", CancellationToken.None);
        var provider = state.Providers.Single(p => p.ProviderId == ServiceProviderIds.EmbeddingsOpenAiEmbedding);
        provider.Fields["ModelId"].Value.Should().Be("text-embedding-3-small");
        provider.Fields["ModelId"].HasValue.Should().BeTrue();
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_PersistsModelIdAndVoiceName_ForOpenAiSpeechSynthesis()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "SpeechSynthesis",
        [
            new ServiceMode("openai", "OpenAI", null, null, true, true)
        ]);

        await service.UpdateServiceProviderFieldsAsync(
            "SpeechSynthesis",
            ServiceProviderIds.SpeechSynthesisOpenAiTts,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["ModelId"] = "tts-1",
                    ["VoiceName"] = "alloy"
                })),
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "SpeechSynthesis",
            ServiceProviderIds.SpeechSynthesisOpenAiTts,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("SpeechSynthesis", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "OpenAI", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "tts-1", StringComparison.Ordinal));

        var state = await service.GetServiceEditorStateAsync("SpeechSynthesis", CancellationToken.None);
        var provider = state.Providers.Single(p => p.ProviderId == ServiceProviderIds.SpeechSynthesisOpenAiTts);
        provider.Fields["ModelId"].Value.Should().Be("tts-1");
        provider.Fields["VoiceName"].Value.Should().Be("alloy");
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_PersistsTextAndImageModelIds_ForHuggingFaceImageGeneration()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "ImageGeneration",
        [
            new ServiceMode("hf-image", "HuggingFace", null, null, true, true)
        ]);

        await service.UpdateServiceProviderFieldsAsync(
            "ImageGeneration",
            ServiceProviderIds.ImageGenerationHuggingFaceInference,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["TextToImageModelId"] = "Tongyi-MAI/Z-Image-Turbo",
                    ["ImageToImageModelId"] = "black-forest-labs/FLUX.2-dev"
                })),
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "ImageGeneration",
            ServiceProviderIds.ImageGenerationHuggingFaceInference,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("ImageGeneration", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "HuggingFace", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "Tongyi-MAI/Z-Image-Turbo", StringComparison.Ordinal));

        var state = await service.GetServiceEditorStateAsync("ImageGeneration", CancellationToken.None);
        var provider = state.Providers.Single(p => p.ProviderId == ServiceProviderIds.ImageGenerationHuggingFaceInference);
        provider.Fields["TextToImageModelId"].Value.Should().Be("Tongyi-MAI/Z-Image-Turbo");
        provider.Fields["ImageToImageModelId"].Value.Should().Be("black-forest-labs/FLUX.2-dev");
    }

    [TestMethod]
    public async Task SetServiceActiveProviderAsync_PreservesInactiveCloudModes_WhenSwitchingProviders()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "SpeechTranscription",
        [
            new ServiceMode("local", "LocalServiceHosts:SpeechTranscriptionBaseUrl", null, null, true, true),
            new ServiceMode("google", "GoogleGeminiApi", null, null, true, false)
        ]);

        await service.UpdateServiceProviderFieldsAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
                {
                    ["ModelId"] = "gemini-2.5-flash"
                })),
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText,
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
            CancellationToken.None);

        var modesAfterLocalSwitch = await service.GetServiceModesAsync("SpeechTranscription", CancellationToken.None);
        modesAfterLocalSwitch.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "GoogleGeminiApi", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "gemini-2.5-flash", StringComparison.Ordinal)
            && !mode.IsDefault);

        await service.SetServiceActiveProviderAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionGoogleSpeechToText,
            CancellationToken.None);

        var modesAfterSwitchBack = await service.GetServiceModesAsync("SpeechTranscription", CancellationToken.None);
        modesAfterSwitchBack.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "GoogleGeminiApi", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "gemini-2.5-flash", StringComparison.Ordinal)
            && mode.IsDefault);
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_AllowsFoundryConnectionFields()
    {
        await using var db = CreateDbContext();
        SeedEmptyProviderSection(db, "AzureOpenAiEmbedding");
        SeedEmptyProviderSection(db, "Embeddings");
        var configuration = BuildChatOnlyFoundryConfiguration();
        var service = CreateService(db, configuration);

        await service.UpdateServiceProviderFieldsAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
            {
                ["Endpoint"] = "https://embeddings.example.openai.azure.com/",
                ["ApiKey"] = "foundry-embedding-key",
                ["Deployment"] = "text-embedding-3-small",
                ["TimeoutSeconds"] = "300",
            })),
            CancellationToken.None);

        var embeddingSection = await service.GetSectionAsync("AzureOpenAiEmbedding", CancellationToken.None);
        embeddingSection.Should().NotBeNull();
        embeddingSection!.Payload["Endpoint"]!.GetValue<string>()
            .Should().Be("https://embeddings.example.openai.azure.com/");
        embeddingSection.SecretHasValue["ApiKey"].Should().BeTrue();

        var modes = await service.GetServiceModesAsync("Embeddings", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "AzureOpenAiEmbedding", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "text-embedding-3-small", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateServiceProviderFieldsAsync_RejectsSharedCloudConnectionFields()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "SpeechTranscription",
        [
            new ServiceMode("openai", "OpenAI", "whisper-1", null, true, true)
        ]);

        var act = async () => await service.UpdateServiceProviderFieldsAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionOpenAiAudio,
            new ProviderFieldsUpdateRequest(JF(new Dictionary<string, string?>
            {
                ["ApiKey"] = "should-not-write"
            })),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("provider connection configuration", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("Unknown field", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SetServiceActiveProviderAsync_CreatesExplicitMode_WhenConnectionIsReady()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);

        var state = await service.SetServiceActiveProviderAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch,
            CancellationToken.None);

        state.ActiveProviderId.Should().Be(ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch);

        var modes = await service.GetServiceModesAsync("SpeechTranscription", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "AzureSpeechService", StringComparison.Ordinal)
            && mode.Enabled
            && mode.IsDefault);
    }

    [TestMethod]
    public async Task GetServiceEditorState_FoundryChatOnly_SurfacesFoundryServiceProviders()
    {
        await using var db = CreateDbContext();
        var configuration = BuildChatOnlyFoundryConfiguration();
        var service = CreateService(db, configuration);

        var images = await service.GetServiceEditorStateAsync("ImageGeneration", CancellationToken.None);
        var foundryImages = images.Providers.Should().ContainSingle(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.ImageGenerationAzureOpenAiImages, StringComparison.Ordinal))
            .Subject;
        foundryImages.ConnectionConfigured.Should().BeTrue(
            "Images/Embeddings inherit Endpoint/ApiKey from chat AzureOpenAI when dedicated sections are empty.");
        foundryImages.RelatedChatConnectionConfigured.Should().BeTrue();
        foundryImages.ConnectionMissingFields.Should().BeEmpty();

        var embeddings = await service.GetServiceEditorStateAsync("Embeddings", CancellationToken.None);
        var foundryEmbeddings = embeddings.Providers.Should().ContainSingle(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding, StringComparison.Ordinal))
            .Subject;
        foundryEmbeddings.ConnectionConfigured.Should().BeTrue();
        foundryEmbeddings.RelatedChatConnectionConfigured.Should().BeTrue();

        var speech = await service.GetServiceEditorStateAsync("SpeechTranscription", CancellationToken.None);
        var foundrySpeech = speech.Providers.Should().ContainSingle(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch, StringComparison.Ordinal))
            .Subject;
        foundrySpeech.ConnectionConfigured.Should().BeFalse(
            "Speech still needs AzureSpeechService credentials; chat Foundry alone is not enough to activate.");
        foundrySpeech.RelatedChatConnectionConfigured.Should().BeTrue(
            "Chat-only Foundry setup must still surface Foundry speech so the user can continue configuration.");
    }

    [TestMethod]
    public async Task EnsureServiceModeExistsAsync_SeedsImagesConnectionFromFoundryChat()
    {
        await using var db = CreateDbContext();
        SeedEmptyProviderSection(db, "AzureOpenAiImages");
        var configuration = BuildChatOnlyFoundryConfiguration();
        var service = CreateService(db, configuration);

        await service.EnsureServiceModeExistsAsync(
            "ImageGeneration",
            ServiceProviderIds.ImageGenerationAzureOpenAiImages,
            CancellationToken.None);

        var imagesSection = await service.GetSectionAsync("AzureOpenAiImages", CancellationToken.None);
        imagesSection.Should().NotBeNull();
        imagesSection!.Payload["Endpoint"]!.GetValue<string>()
            .Should().Be("https://my-foundry-resource.openai.azure.com/");
        imagesSection.SecretHasValue["ApiKey"].Should().BeTrue();
    }

    [TestMethod]
    public async Task EnsureServiceModeExistsAsync_DoesNotInventModelId_ForLocalSpeechTranscription()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);

        await service.EnsureServiceModeExistsAsync(
            "SpeechTranscription",
            ServiceProviderIds.SpeechTranscriptionLocalAsrHttp,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("SpeechTranscription", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "LocalServiceHosts:SpeechTranscriptionBaseUrl", StringComparison.Ordinal)
            && mode.ModelId == null
            && !mode.Enabled
            && !mode.IsDefault);
    }

    [TestMethod]
    public async Task EnsureServiceModeExistsAsync_DoesNotInventModelId_ForLocalSpeechSynthesis()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);

        await service.EnsureServiceModeExistsAsync(
            "SpeechSynthesis",
            ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("SpeechSynthesis", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "LocalServiceHosts:SpeechSynthesisBaseUrl", StringComparison.Ordinal)
            && mode.ModelId == null
            && !mode.Enabled
            && !mode.IsDefault);
    }

    [TestMethod]
    public async Task EnsureServiceModeExistsAsync_DoesNotInventModelId_ForHuggingFaceSpeechSynthesis()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);

        await service.EnsureServiceModeExistsAsync(
            "SpeechSynthesis",
            ServiceProviderIds.SpeechSynthesisHuggingFaceInference,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("SpeechSynthesis", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "HuggingFace", StringComparison.Ordinal)
            && mode.ModelId == null
            && !mode.Enabled
            && !mode.IsDefault);
    }

    [TestMethod]
    public async Task EnsureServiceModeExistsAsync_DoesNotInventModelId_ForLocalImageGeneration()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);

        await service.EnsureServiceModeExistsAsync(
            "ImageGeneration",
            ServiceProviderIds.ImageGenerationLocalSdHttp,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("ImageGeneration", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "LocalServiceHosts:ImageGenerationBaseUrl", StringComparison.Ordinal)
            && mode.ModelId == null
            && !mode.Enabled
            && !mode.IsDefault);
    }

    [TestMethod]
    public async Task EnsureServiceModeExistsAsync_DoesNotAlterExistingLocalImageMode_WhenModelIdIsNull()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "ImageGeneration",
        [
            new ServiceMode(
                "local",
                "LocalServiceHosts:ImageGenerationBaseUrl",
                null,
                null,
                Enabled: true,
                IsDefault: true),
        ]);

        await service.EnsureServiceModeExistsAsync(
            "ImageGeneration",
            ServiceProviderIds.ImageGenerationLocalSdHttp,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("ImageGeneration", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "LocalServiceHosts:ImageGenerationBaseUrl", StringComparison.Ordinal)
            && mode.ModelId == null);
    }

    [TestMethod]
    public async Task SetServiceModeModelIdAsync_PersistsOntoActiveEnabledDefaultMode()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "SpeechSynthesis",
        [
            new ServiceMode(
                "local",
                "LocalServiceHosts:SpeechSynthesisBaseUrl",
                null,
                null,
                Enabled: true,
                IsDefault: true),
        ]);

        await service.SetServiceModeModelIdAsync(
            "SpeechSynthesis",
            "chatterbox",
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("SpeechSynthesis", CancellationToken.None);
        modes.Should().Contain(mode =>
            mode.IsDefault
            && mode.Enabled
            && string.Equals(mode.ModelId, "chatterbox", StringComparison.Ordinal));
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"service-editor-validation-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static void SeedServiceModes(ApplicationDbContext db, string serviceName, IReadOnlyList<ServiceMode> modes)
    {
        var payload = new JsonObject();
        ServiceModesPayload.WriteModesFor(payload, serviceName, modes, modes.FirstOrDefault(mode => mode.IsDefault)?.ModeId);
        db.ApplicationSettings.Add(new ApplicationSetting
        {
            SectionName = ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = payload.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedEmptyProviderSection(ApplicationDbContext db, string sectionName)
    {
        db.ApplicationSettings.Add(new ApplicationSetting
        {
            SectionName = sectionName,
            SchemaVersion = 1,
            JsonValue = "{}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static IConfiguration BuildChatOnlyFoundryConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = "tests",
            ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
            ["Ui:RootPath"] = "./ui",
            ["AzureOpenAI:Resource"] = "my-foundry-resource",
            ["AzureOpenAI:ApiKey"] = "test-foundry-chat-key",
            ["AzureOpenAI:ApiVersion"] = "2025-04-01-preview",
            ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:MediaBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:DocumentIntelligenceBaseUrl"] = "http://localhost:5001",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static IConfiguration BuildConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = "tests",
            ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
            ["Ui:RootPath"] = "./ui",
            ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp",
            ["ServiceRouting:Containers:guideants-ai:BaseUrl"] = "http://localhost:8110/sandbox",
            ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:MediaBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:DocumentIntelligenceBaseUrl"] = "http://localhost:5001",
            ["AzureSpeechService:Endpoint"] = "https://speech.example.com/",
            ["AzureSpeechService:ApiKey"] = "test-speech-key",
            ["AzureSpeechService:Region"] = "eastus2",
            ["AzureOpenAiEmbedding:Endpoint"] = "https://embedding-api.example.com/",
            ["AzureOpenAiEmbedding:ApiKey"] = "test-embedding-key",
            ["AzureOpenAiEmbedding:Deployment"] = "text-embedding-3-small",
            ["AzureOpenAiImages:Endpoint"] = "https://image-api.example.com/",
            ["AzureOpenAiImages:ApiKey"] = "test-api-key",
            ["AzureOpenAiImages:Deployment"] = "flux-1",
            ["AzureOpenAiImages:EditModelDeployment"] = "flux-1-edit",
            ["AzureDocumentIntelligence:Endpoint"] = "https://doc-intel.example.com/",
            ["AzureDocumentIntelligence:ApiKey"] = "test-doc-intel-key",
            ["GoogleGeminiApi:ApiKey"] = "test-gemini-key",
            ["HuggingFace:Token"] = "hf_test_token",
            ["HuggingFace:RouterBaseUrl"] = "https://router.huggingface.co/v1",
            ["OpenAI:ApiKey"] = "test-openai-key"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static ApplicationSettingsService CreateService(ApplicationDbContext db, IConfiguration configuration)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);

        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(value => value.CurrentValue).Returns(new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            }
        });
        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object);
    }

    private static IReadOnlyDictionary<string, JsonElement> JF(Dictionary<string, string?> fields)
    {
        var json = JsonSerializer.Serialize(fields);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }
}


