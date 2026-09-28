using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class SettingsModelContextWindowTests
{
    [TestMethod]
    public void CreateRequest_AcceptsContextWindow()
    {
        var request = new CreateSettingsModelRequest(
            ModelId: "test-model",
            DisplayName: "Test Model",
            Provider: "openai-chat",
            Description: null,
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: null,
            ContextWindowTokens: 200_000,
            MaxOutputTokens: 64_000);

        request.ContextWindowTokens.Should().Be(200_000);
        request.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void CreateRequest_DefaultsContextWindowToNull()
    {
        var request = new CreateSettingsModelRequest(
            ModelId: "test-model",
            DisplayName: "Test Model",
            Provider: "openai-chat",
            Description: null,
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: null);

        request.ContextWindowTokens.Should().BeNull();
        request.MaxOutputTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateModelAsync_PersistsAndReturnsContextWindow()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var dto = await service.CreateModelAsync(CreateRequest("m-create", 131_072, 8_191));

        dto.ContextWindowTokens.Should().Be(131_072);
        dto.MaxOutputTokens.Should().Be(8_191);
        var row = await db.Models.SingleAsync(x => x.ModelId == "m-create");
        row.ContextWindowTokens.Should().Be(131_072);
        row.MaxOutputTokens.Should().Be(8_191);
    }

    [TestMethod]
    public async Task CreateModelAsync_WithoutValues_LeavesNull()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var dto = await service.CreateModelAsync(CreateRequest("m-none", null, null));

        dto.ContextWindowTokens.Should().BeNull();
        dto.MaxOutputTokens.Should().BeNull();
        var row = await db.Models.SingleAsync(x => x.ModelId == "m-none");
        row.ContextWindowTokens.Should().BeNull();
        row.MaxOutputTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task UpdateModelAsync_PersistsAndReturnsNewContextWindow()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateModelAsync(CreateRequest("m-update", 100_003, 4_001));

        var dto = await service.UpdateModelAsync("m-update", UpdateRequest("m-update", 262_147, 16_381));

        dto.Should().NotBeNull();
        dto!.ContextWindowTokens.Should().Be(262_147);
        dto.MaxOutputTokens.Should().Be(16_381);
        var row = await db.Models.SingleAsync(x => x.ModelId == "m-update");
        row.ContextWindowTokens.Should().Be(262_147);
        row.MaxOutputTokens.Should().Be(16_381);
    }

    [TestMethod]
    public async Task UpdateModelAsync_WithNullValues_ClearsExistingValues()
    {
        // The settings PUT is a full replacement: null means "clear", not "leave unchanged".
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateModelAsync(CreateRequest("m-clear", 100_003, 4_001));

        var dto = await service.UpdateModelAsync("m-clear", UpdateRequest("m-clear", null, null));

        dto!.ContextWindowTokens.Should().BeNull();
        dto.MaxOutputTokens.Should().BeNull();
        var row = await db.Models.SingleAsync(x => x.ModelId == "m-clear");
        row.ContextWindowTokens.Should().BeNull();
        row.MaxOutputTokens.Should().BeNull();
    }

    private static CreateSettingsModelRequest CreateRequest(string id, int? window, int? maxOutput) => new(
        ModelId: id,
        DisplayName: "Test Model",
        Provider: "openai-chat",
        Description: null,
        ReasoningChoicesJson: null,
        RuntimeConfigJson: null,
        IsActive: true,
        DisplayOrder: null,
        ContextWindowTokens: window,
        MaxOutputTokens: maxOutput);

    private static UpdateSettingsModelRequest UpdateRequest(string id, int? window, int? maxOutput) => new(
        ModelId: id,
        DisplayName: "Test Model",
        Provider: "openai-chat",
        Description: null,
        ReasoningChoicesJson: null,
        RuntimeConfigJson: null,
        IsActive: true,
        DisplayOrder: null,
        ContextWindowTokens: window,
        MaxOutputTokens: maxOutput);

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"settings-model-context-window-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static ApplicationSettingsService CreateService(ApplicationDbContext db)
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

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SettingsSecrets:ActiveKeyId"] = "tests",
                ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            })
            .Build();

        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object);
    }
}
