using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsServiceRuntimeDependencyOverrideTests
{
    [TestMethod]
    public async Task SetRuntimeDependencyOverrideAsync_RejectsMalformedUrl()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>()));

        var act = async () => await service.SetRuntimeDependencyOverrideAsync(
            "LocalServiceHosts:EmbeddingsBaseUrl",
            "not-a-url");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be an absolute URL*");
    }

    [TestMethod]
    public async Task SetRuntimeDependencyOverrideAsync_RejectsNonHttpScheme()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>()));

        var act = async () => await service.SetRuntimeDependencyOverrideAsync(
            "LocalServiceHosts:EmbeddingsBaseUrl",
            "ftp://localhost:8110");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must use http:// or https://*");
    }

    [TestMethod]
    public async Task SetRuntimeDependencyOverrideAsync_RejectsLlamaBaseUrlWithoutRequiredPath()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>()));

        var act = async () => await service.SetRuntimeDependencyOverrideAsync(
            "LlamaCpp:BaseUrl",
            "http://localhost:8110");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must include the '/llama-cpp' path*");
    }

    [TestMethod]
    public async Task SetRuntimeDependencyOverrideAsync_AcceptsValidLlamaBaseUrl()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>()));

        var updated = await service.SetRuntimeDependencyOverrideAsync(
            "LlamaCpp:BaseUrl",
            "  http://localhost:8110/llama-cpp  ");

        updated.Should().NotBeNull();
        updated!.CurrentValue.Should().Be("http://localhost:8110/llama-cpp");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"settings-runtime-deps-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static ApplicationSettingsService CreateService(ApplicationDbContext db, IConfiguration configuration)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);

        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(value => value.CurrentValue).Returns(CreateValidSecretsOptions());

        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();

        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object,
            runtimeProfileResolver.Object);
    }

    private static SettingsSecretsOptions CreateValidSecretsOptions() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
        }
    };

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        var defaults = new Dictionary<string, string?>
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

        foreach (var pair in values)
        {
            defaults[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .Build();
    }
}
