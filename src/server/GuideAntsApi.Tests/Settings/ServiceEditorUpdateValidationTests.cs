using FluentAssertions;
using GuideAntsApi.Configuration;
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
            new ProviderFieldsUpdateRequest(
                new Dictionary<string, string?>
                {
                    ["TimeoutSeconds"] = "30",
                    ["NotARealField"] = "x"
                }),
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
            new ProviderFieldsUpdateRequest(
                new Dictionary<string, string?>
                {
                    ["TimeoutSeconds"] = "not-a-number"
                }),
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
            new ProviderFieldsUpdateRequest(
                new Dictionary<string, string?>
                {
                    ["ModelId"] = "sentence-transformers/all-MiniLM-L6-v2"
                }),
            CancellationToken.None);

        await service.SetServiceActiveProviderAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsHuggingFaceInference,
            CancellationToken.None);

        var modes = await service.GetServiceModesAsync("Embeddings", CancellationToken.None);
        modes.Should().Contain(mode =>
            string.Equals(mode.ProviderSection, "HuggingFace", StringComparison.Ordinal)
            && string.Equals(mode.ModelId, "sentence-transformers/all-MiniLM-L6-v2", StringComparison.Ordinal));

        var state = await service.GetServiceEditorStateAsync("Embeddings", CancellationToken.None);
        var provider = state.Providers.Single(p => p.ProviderId == ServiceProviderIds.EmbeddingsHuggingFaceInference);
        provider.Fields["ModelId"].Value.Should().Be("sentence-transformers/all-MiniLM-L6-v2");
        provider.Fields["ModelId"].HasValue.Should().BeTrue();
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
            new ProviderFieldsUpdateRequest(
                new Dictionary<string, string?>
                {
                    ["ModelId"] = "gemini-2.5-flash"
                }),
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
    public async Task UpdateServiceProviderFieldsAsync_RejectsConnectionFields()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        SeedServiceModes(db, "Embeddings",
        [
            new ServiceMode("azure", "AzureOpenAiEmbedding", null, null, true, true)
        ]);

        var act = async () => await service.UpdateServiceProviderFieldsAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
            new ProviderFieldsUpdateRequest(
                new Dictionary<string, string?>
                {
                    ["Endpoint"] = "https://embedding-api.example.com/"
                }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("provider connection configuration", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SetServiceActiveProviderAsync_RejectsMissingExplicitMode()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);

        var act = async () => await service.SetServiceActiveProviderAsync(
            "Embeddings",
            ServiceProviderIds.EmbeddingsHuggingFaceInference,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("no explicit service mode", StringComparison.OrdinalIgnoreCase));

        var modes = await service.GetServiceModesAsync("Embeddings", CancellationToken.None);
        modes.Should().NotContain(mode => string.Equals(mode.ProviderSection, "HuggingFace", StringComparison.Ordinal));
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
            ["HuggingFace:RouterBaseUrl"] = "https://router.huggingface.co/v1"
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

        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();

        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object,
            runtimeProfileResolver.Object);
    }
}
