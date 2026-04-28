using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
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
public sealed class ApplicationSettingsServiceModelTests
{
    [TestMethod]
    public async Task CreateModelAsync_RejectsAnthropicThinkingChoices_WhenBudgetMissing()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>
        {
            ["Anthropic:ThinkingBudgetMinimal"] = "1024",
            ["Anthropic:ThinkingBudgetMedium"] = "2048",
            ["Anthropic:ThinkingBudgetHigh"] = "3072"
        }));

        Func<Task> act = () => service.CreateModelAsync(new CreateSettingsModelRequest(
            ModelId: "claude-haiku-4-5-20251001",
            DisplayName: "Claude Haiku 4.5",
            Provider: "anthropic",
            Description: null,
            ReasoningChoicesJson: "[\"minimal\",\"low\",\"medium\",\"high\"]",
            LocalRuntimeJson: null,
            IsActive: true,
            DisplayOrder: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Anthropic:ThinkingBudgetLow*");
    }

    [TestMethod]
    public async Task CreateModelAsync_RejectsAnthropicThinkingChoices_WhenOnlyLegacyBudgetAliasesConfigured()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>
        {
            ["ANTHROPIC_THINKING_BUDGET_MINIMAL"] = "1024",
            ["ANTHROPIC_THINKING_BUDGET_LOW"] = "1536",
            ["ANTHROPIC_THINKING_BUDGET_MEDIUM"] = "2048",
            ["ANTHROPIC_THINKING_BUDGET_HIGH"] = "3072"
        }));

        Func<Task> act = () => service.CreateModelAsync(new CreateSettingsModelRequest(
            ModelId: "claude-haiku-4-5-20251001",
            DisplayName: "Claude Haiku 4.5",
            Provider: "anthropic",
            Description: null,
            ReasoningChoicesJson: "[\"minimal\",\"low\",\"medium\",\"high\"]",
            LocalRuntimeJson: null,
            IsActive: true,
            DisplayOrder: null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Anthropic:ThinkingBudgetMinimal*");
    }

    [TestMethod]
    public async Task CreateModelAsync_AllowsAnthropicThinkingChoices_WhenBudgetsConfigured()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration(new Dictionary<string, string?>
        {
            ["Anthropic:ThinkingBudgetMinimal"] = "1024",
            ["Anthropic:ThinkingBudgetLow"] = "1536",
            ["Anthropic:ThinkingBudgetMedium"] = "2048",
            ["Anthropic:ThinkingBudgetHigh"] = "3072"
        }));

        var created = await service.CreateModelAsync(new CreateSettingsModelRequest(
            ModelId: "claude-haiku-4-5-20251001",
            DisplayName: "Claude Haiku 4.5",
            Provider: "anthropic",
            Description: null,
            ReasoningChoicesJson: "[\"minimal\",\"low\",\"medium\",\"high\"]",
            LocalRuntimeJson: null,
            IsActive: true,
            DisplayOrder: null));

        created.ReasoningChoicesJson.Should().Be("[\"minimal\",\"low\",\"medium\",\"high\"]");
    }

    [TestMethod]
    public void SettingsSectionRegistry_Anthropic_DeclaresCanonicalBudgetKeys()
    {
        var anthropic = new SettingsSectionRegistry().All
            .Single(section => section.SectionName == "Anthropic");

        anthropic.Properties
            .Select(property => property.CanonicalKey)
            .Should()
            .Contain([
                "Anthropic:ThinkingBudgetMinimal",
                "Anthropic:ThinkingBudgetLow",
                "Anthropic:ThinkingBudgetMedium",
                "Anthropic:ThinkingBudgetHigh"
            ]);
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
