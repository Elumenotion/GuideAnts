using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ServiceEditorMetadataProviderTests
{
    [TestMethod]
    public void GetProviderFields_LocalSpeechSynthesis_VoiceNameIsCatalogDrivenNotHardcodedEnum()
    {
        var metadataProvider = new ServiceEditorMetadataProvider();

        var fields = metadataProvider.GetProviderFields("SpeechSynthesis", "SpeechSynthesis.LocalTts.Http");

        fields.Select(field => field.Name).Should().Equal("TimeoutSeconds", "VoiceName");
        var voiceField = fields.Single(field => field.Name == "VoiceName");

        // Voice options are catalog-driven (voiceInput -> voice-pack API /
        // runtime speaker list / instruct text), not a static server enum.
        // The old hardcoded en_us_cv_001/en_gb_cv_002/es_cv_001/fr_cv_001
        // list has been removed (RULES I4).
        voiceField.Kind.Should().Be("text");
        voiceField.EnumOptions.Should().BeNullOrEmpty();
        fields.Should().NotContain(field => field.Name == "LanguageCode" || field.Name == "Speed");
    }

    [TestMethod]
    public async Task GetSchemaAndMetadata_AllProvidersHaveFieldMetadataAndConfigurationOwner()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();
        var service = CreateService(db, configuration);
        var metadataProvider = new ServiceEditorMetadataProvider();

        var schema = await service.GetSchemaAsync();

        foreach (var provider in schema.Providers)
        {
            var fields = metadataProvider.GetProviderFields(provider.ServiceId, provider.ProviderId);
            fields.Should().NotBeEmpty($"provider '{provider.ProviderId}' needs metadata for the service editor.");

            var hasProviderSection = !string.IsNullOrWhiteSpace(provider.ProviderSettingsSection)
                || provider.RequiredSectionFields.Count > 0;
            var hasRuntimeDependency = provider.RequiredRuntimeKeys.Count > 0;
            (hasProviderSection || hasRuntimeDependency)
                .Should().BeTrue($"provider '{provider.ProviderId}' must declare a settings owner or runtime dependency.");
        }
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"service-editor-metadata-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
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
            ["OpenRouter:ApiKey"] = "test-openrouter-key",
            ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
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
