using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsServiceChatDefaultsTests
{
    [TestMethod]
    public async Task UpdateSectionAsync_RejectsReasoningEffort_WhenDefaultModelHasNoReasoningChoices()
    {
        await using var db = CreateDbContext();
        db.Models.Add(new Model
        {
            ModelId = "gemini-2.5-flash",
            DisplayName = "Gemini 2.5 Flash",
            Provider = "google-gemini-chat",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, BuildConfiguration());
        var section = await service.GetSectionAsync("ChatDefaults");

        section.Should().NotBeNull();

        var result = await service.UpdateSectionAsync(
            "ChatDefaults",
            new UpdateSettingsSectionRequest(
                section!.RowVersion,
                new JsonObject
                {
                    ["DefaultModelId"] = "gemini-2.5-flash",
                    ["OverrideAllChatModels"] = true,
                    ["Temperature"] = 0.7,
                    ["TopP"] = 0.9,
                    ["ReasoningEffort"] = "enabled",
                    ["SamplingParametersJson"] = null
                }));

        result.Section.Should().BeNull();
        result.ConcurrencyConflict.Should().BeFalse();
        result.ValidationErrors.Should().ContainSingle(error =>
            error.Contains("does not declare any reasoning choices", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateSectionAsync_AllowsReasoningEffort_WhenDefaultModelDeclaresMatchingChoices()
    {
        await using var db = CreateDbContext();
        db.Models.Add(new Model
        {
            ModelId = "gpt-5-mini",
            DisplayName = "GPT-5 mini",
            Provider = "openai-chat",
            ReasoningChoicesJson = "[\"minimal\",\"low\",\"medium\",\"high\"]",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, BuildConfiguration());
        var section = await service.GetSectionAsync("ChatDefaults");

        section.Should().NotBeNull();

        var result = await service.UpdateSectionAsync(
            "ChatDefaults",
            new UpdateSettingsSectionRequest(
                section!.RowVersion,
                new JsonObject
                {
                    ["DefaultModelId"] = "gpt-5-mini",
                    ["OverrideAllChatModels"] = true,
                    ["Temperature"] = 0.7,
                    ["TopP"] = 0.9,
                    ["ReasoningEffort"] = "high",
                    ["SamplingParametersJson"] = null
                }));

        result.ConcurrencyConflict.Should().BeFalse();
        result.ValidationErrors.Should().BeEmpty();
        result.Section.Should().NotBeNull();
        result.Section!.Payload["ReasoningEffort"]!.GetValue<string>().Should().Be("high");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"chat-defaults-validation-{Guid.NewGuid():N}")
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
            ["GoogleGeminiApi:ApiKey"] = "test-gemini-key"
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
