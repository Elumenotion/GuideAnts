using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
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
public sealed class ApplicationSettingsServiceDeepTests2
{
    // ===== Models CRUD =====

    [TestMethod]
    public async Task CreateModelAsync_Persists_And_GetModelsAsync_Returns_Ordered()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        await service.CreateModelAsync(NewModelRequest("b-model", displayOrder: 2));
        await service.CreateModelAsync(NewModelRequest("a-model", displayOrder: 1));

        var models = await service.GetModelsAsync();

        models.Should().HaveCount(2);
        models[0].ModelId.Should().Be("a-model");
    }

    [TestMethod]
    public async Task CreateModelAsync_Throws_On_Duplicate()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateModelAsync(NewModelRequest("dup"));

        var act = async () => await service.CreateModelAsync(NewModelRequest("dup"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [TestMethod]
    public async Task CreateModelAsync_Throws_For_Invalid_ReasoningChoicesJson()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateModelAsync(
            NewModelRequest("bad-reasoning") with { ReasoningChoicesJson = "{ not json" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*JSON array of strings*");
    }

    [TestMethod]
    public async Task CreateModelAsync_Normalizes_ReasoningChoices()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var created = await service.CreateModelAsync(
            NewModelRequest("norm") with { ReasoningChoicesJson = "[\"high\",\" high \",\"\",\"low\"]" });

        created.ReasoningChoicesJson.Should().Contain("high").And.Contain("low");
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(created.ReasoningChoicesJson!)!
            .Should().BeEquivalentTo(["high", "low"]);
    }

    [TestMethod]
    public async Task UpdateModelAsync_Updates_Existing_And_Returns_Null_For_Missing()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateModelAsync(NewModelRequest("m1"));

        var updated = await service.UpdateModelAsync("m1", new UpdateSettingsModelRequest(
            ModelId: "m1",
            DisplayName: "Renamed",
            Provider: "openai-chat",
            Description: "new desc",
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: false,
            DisplayOrder: 9));

        updated.Should().NotBeNull();
        updated!.DisplayName.Should().Be("Renamed");
        updated.IsActive.Should().BeFalse();

        (await service.UpdateModelAsync("ghost", new UpdateSettingsModelRequest(
            "ghost", "X", "openai-chat", null, null, null, true, null))).Should().BeNull();
    }

    [TestMethod]
    public async Task UpdateModelAsync_Throws_For_Blank_RouteId()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.UpdateModelAsync("   ", new UpdateSettingsModelRequest(
            "x", "X", "openai-chat", null, null, null, true, null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Route modelId is required*");
    }

    [TestMethod]
    public async Task DeleteModelAsync_Returns_True_Then_False()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateModelAsync(NewModelRequest("del"));

        (await service.DeleteModelAsync("del")).Should().BeTrue();
        (await service.DeleteModelAsync("del")).Should().BeFalse();
    }

    [TestMethod]
    public async Task GetChatTargetsAsync_Reports_LocalRuntime_Flag()
    {
        await using var db = CreateDbContext();
        db.Models.Add(new Model
        {
            ModelId = "cloud",
            DisplayName = "Cloud",
            Provider = "openai-chat",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.Models.Add(new Model
        {
            ModelId = "local",
            DisplayName = "Local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = """{"routerModelId":"router","runtimeProfileId":"profile-a"}""",
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var targets = await service.GetChatTargetsAsync();

        targets.Single(t => t.ModelId == "cloud").HasLocalRuntime.Should().BeFalse();
        targets.Single(t => t.ModelId == "local").HasLocalRuntime.Should().BeTrue();
    }

    // ===== helpers =====

    private static CreateSettingsModelRequest NewModelRequest(string modelId, int? displayOrder = null) =>
        new(
            ModelId: modelId,
            DisplayName: modelId,
            Provider: "openai-chat",
            Description: "desc",
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: displayOrder);


    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"settings-deep2-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static ApplicationSettingsService CreateService(ApplicationDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SettingsSecrets:ActiveKeyId"] = "tests",
                ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
                ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp"
            })
            .Build();

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
}
