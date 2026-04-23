using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Settings;

/// <summary>
/// Phase D (R-5.5 / R-8.1): <see cref="ApplicationSettingsService.GetConnectionUsageAsync"/>
/// composes two sources — <c>ServiceModes</c> rows that point at a section, and
/// active-assistant-referenced catalog models whose provider maps to that section.
/// These tests pin that composition so the Connections "Used by services" chips
/// stay honest as the schema evolves.
/// </summary>
[TestClass]
public sealed class ApplicationSettingsServiceConnectionUsageTests
{
    [TestMethod]
    public async Task GetConnectionUsageAsync_SectionWithTwoModesPointingToIt_ListsBothModes()
    {
        await using var db = CreateDbContext();
        SeedServiceModes(db, RoutedServiceNames.SpeechTranscription, new[]
        {
            new ServiceMode("default", "AzureSpeechService", ModelId: null, RequestPresetJson: null, Enabled: true, IsDefault: true),
            new ServiceMode("backup", "AzureSpeechService", ModelId: null, RequestPresetJson: null, Enabled: true, IsDefault: false),
            new ServiceMode("local", "LocalServiceHosts:SpeechTranscriptionBaseUrl", ModelId: null, RequestPresetJson: null, Enabled: true, IsDefault: false)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, BuildConfiguration());

        var usage = await service.GetConnectionUsageAsync("AzureSpeechService");

        usage.Section.Should().Be("AzureSpeechService");
        usage.Modes.Should().HaveCount(2);
        usage.Modes.Should().Contain(m => m.Service == RoutedServiceNames.SpeechTranscription && m.ModeId == "default" && m.IsDefault);
        usage.Modes.Should().Contain(m => m.Service == RoutedServiceNames.SpeechTranscription && m.ModeId == "backup" && !m.IsDefault);
        usage.ChatTargets.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetConnectionUsageAsync_ChatTargetReferencedByTwoAssistants_ReportsCountOfTwo()
    {
        await using var db = CreateDbContext();
        db.Models.Add(new Model
        {
            ModelId = "gpt-4o",
            DisplayName = "GPT-4o",
            // After the OpenAI-platform / Azure-OpenAI provider split, bare
            // "azure-openai" is no longer a recognized catalog Provider value
            // (it was ambiguous about chat-vs-responses). Azure catalog rows
            // now carry the explicit "azure-openai-chat" /
            // "azure-openai-responses" strings; the RenameOpenAiChatProviders
            // migration flips existing rows to the chat variant.
            Provider = "azure-openai-chat",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.Assistants.Add(NewAssistant("Alpha", modelId: "gpt-4o", isActive: true));
        db.Assistants.Add(NewAssistant("Beta", modelId: "gpt-4o", isActive: true));
        db.Assistants.Add(NewAssistant("Gamma", modelId: "gpt-4o", isActive: false));  // inactive, should not count
        await db.SaveChangesAsync();

        var service = CreateService(db, BuildConfiguration());

        var usage = await service.GetConnectionUsageAsync("AzureOpenAI");

        usage.Section.Should().Be("AzureOpenAI");
        usage.Modes.Should().BeEmpty();
        usage.ChatTargets.Should().HaveCount(1);
        usage.ChatTargets[0].ModelId.Should().Be("gpt-4o");
        usage.ChatTargets[0].AssistantCount.Should().Be(2);
    }

    [TestMethod]
    public async Task GetConnectionUsageAsync_SectionWithZeroReferences_ReturnsEmptyLists()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());

        var usage = await service.GetConnectionUsageAsync("AzureDocumentIntelligence");

        usage.Section.Should().Be("AzureDocumentIntelligence");
        usage.Modes.Should().NotBeNull().And.BeEmpty();
        usage.ChatTargets.Should().NotBeNull().And.BeEmpty();
    }

    private static Assistant NewAssistant(string name, string modelId, bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ModelId = modelId,
        IsActive = isActive,
        IsGlobal = true,
    };

    private static void SeedServiceModes(ApplicationDbContext db, string serviceName, IReadOnlyList<ServiceMode> modes)
    {
        var payload = new JsonObject();
        ServiceModesPayload.WriteModesFor(payload, serviceName, modes, modes.FirstOrDefault(m => m.IsDefault)?.ModeId);
        db.ApplicationSettings.Add(new ApplicationSetting
        {
            SectionName = ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = payload.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"connection-usage-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
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

    private static IConfiguration BuildConfiguration()
    {
        // Deliberately omit legacy `{Service}:ActiveProviderId` keys. Bootstrap
        // does not synthesize modes from those values, so this fixture only sees
        // the modes explicitly seeded in each test.
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SettingsSecrets:ActiveKeyId"] = "tests",
                ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
                ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp"
            })
            .Build();
    }
}
