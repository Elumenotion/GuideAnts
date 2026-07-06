using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsServiceModelTests
{
    [TestMethod]
    public async Task CreateModelAsync_AllowsAnthropicThinkingChoices_WithoutConnectionOwnedBudgets()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());

        var created = await service.CreateModelAsync(new CreateSettingsModelRequest(
            ModelId: "claude-haiku-4-5-20251001",
            DisplayName: "Claude Haiku 4.5",
            Provider: "anthropic",
            Description: null,
            ReasoningChoicesJson: "[\"minimal\",\"low\",\"medium\",\"high\"]",
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: null));

        created.ReasoningChoicesJson.Should().Be("[\"minimal\",\"low\",\"medium\",\"high\"]");
    }

    [TestMethod]
    public void SettingsSectionRegistry_Anthropic_DoesNotDeclareModelParameterKeys()
    {
        var anthropic = new SettingsSectionRegistry().All
            .Single(section => section.SectionName == "Anthropic");

        anthropic.Properties
            .Select(property => property.CanonicalKey)
            .Should()
            .NotContain([
                "Anthropic:DefaultModel",
                "Anthropic:DefaultMaxTokens",
                "Anthropic:ThinkingBudgetMinimal",
                "Anthropic:ThinkingBudgetLow",
                "Anthropic:ThinkingBudgetMedium",
                "Anthropic:ThinkingBudgetHigh"
            ]);
    }

    [TestMethod]
    public async Task UpdateModelAsync_UsesRouteModelId_AsOpaqueIdentifier_IncludingSlash()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());

        var created = await service.CreateModelAsync(new CreateSettingsModelRequest(
            ModelId: "zai-org/GLM-5.2",
            DisplayName: "DeepSeek",
            Provider: "hf-inference-chat",
            Description: null,
            ReasoningChoicesJson: null,
            RuntimeConfigJson: "{\"runtimeProfileId\":\"huggingface_chat_standard\"}",
            IsActive: true,
            DisplayOrder: null));

        var updated = await service.UpdateModelAsync(
            "zai-org/GLM-5.2",
            new UpdateSettingsModelRequest(
                ModelId: "some-other-id",
                DisplayName: "DeepSeek Updated",
                Provider: created.Provider,
                Description: created.Description,
                ReasoningChoicesJson: created.ReasoningChoicesJson,
                RuntimeConfigJson: created.RuntimeConfigJson,
                IsActive: created.IsActive,
                DisplayOrder: created.DisplayOrder));

        updated.Should().NotBeNull();
        updated!.ModelId.Should().Be("zai-org/GLM-5.2");
        updated.DisplayName.Should().Be("DeepSeek Updated");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"settings-model-validation-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = "tests",
            ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
            ["Ui:RootPath"] = "./ui",
            ["Anthropic:DefaultMaxTokens"] = "64000"
        };

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }
        }

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

