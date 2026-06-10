using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
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

    // ===== RuntimeProfiles CRUD =====

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Persists_And_Getters_Work()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var created = await service.CreateRuntimeProfileAsync(NewProfileRequest("profile_a"));

        created.ProfileId.Should().Be("profile_a");
        (await service.GetRuntimeProfileAsync("profile_a")).Should().NotBeNull();
        (await service.GetRuntimeProfileAsync("missing")).Should().BeNull();
        (await service.GetRuntimeProfilesAsync()).Should().ContainSingle(p => p.ProfileId == "profile_a");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Throws_On_Duplicate()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateRuntimeProfileAsync(NewProfileRequest("dup_profile"));

        var act = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("dup_profile"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_Invalid_ProfileIds()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var blank = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("   "));
        await blank.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Profile ID is required*");

        var tooLong = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest(new string('a', 65)));
        await tooLong.Should().ThrowAsync<InvalidOperationException>().WithMessage("*64 characters or fewer*");

        var badChars = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("bad id!"));
        await badChars.Should().ThrowAsync<InvalidOperationException>().WithMessage("*alphanumeric*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_Invalid_SamplingJson()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateRuntimeProfileAsync(
            NewProfileRequest("p1") with { SamplingParametersJson = "{ not json" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SamplingParametersJson is not valid JSON*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_MinGreaterThanMax()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("p2") with
        {
            SamplingParametersJson = "{\"temperature\":{\"key\":\"temperature\",\"min\":2,\"max\":1,\"default\":1.5}}"
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot exceed max*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_DefaultOutOfRange()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("p3") with
        {
            SamplingParametersJson = "{\"temperature\":{\"key\":\"temperature\",\"min\":0,\"max\":1,\"default\":5}}"
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must be between min*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_BlankParameterKey()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("p4") with
        {
            SamplingParametersJson = "{\"temperature\":{\"key\":\"\",\"min\":0,\"max\":1,\"default\":0.5}}"
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*blank key*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_Invalid_ThinkingJson()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateRuntimeProfileAsync(
            NewProfileRequest("p5") with { ThinkingControlJson = "{ not json" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ThinkingControlJson is not valid JSON*");
    }

    [TestMethod]
    public async Task CreateRuntimeProfileAsync_Rejects_DefaultChoice_Not_In_ChoiceActions()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var act = async () => await service.CreateRuntimeProfileAsync(NewProfileRequest("p6") with
        {
            ThinkingControlJson = "{\"defaultChoice\":\"High\",\"choiceActions\":{\"None\":[]}}"
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is not defined in choiceActions*");
    }

    [TestMethod]
    public async Task UpdateRuntimeProfileAsync_Mismatch_RouteAndPayload_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateRuntimeProfileAsync(NewProfileRequest("profile_x"));

        var act = async () => await service.UpdateRuntimeProfileAsync("profile_x", NewUpdateProfileRequest("other"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must match payload*");
    }

    [TestMethod]
    public async Task UpdateRuntimeProfileAsync_Returns_Null_For_Missing()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        (await service.UpdateRuntimeProfileAsync("ghost", NewUpdateProfileRequest("ghost"))).Should().BeNull();
    }

    [TestMethod]
    public async Task UpdateRuntimeProfileAsync_Updates_Existing()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateRuntimeProfileAsync(NewProfileRequest("profile_u"));

        var updated = await service.UpdateRuntimeProfileAsync(
            "profile_u",
            NewUpdateProfileRequest("profile_u") with { DisplayName = "Renamed Profile" });

        updated.Should().NotBeNull();
        updated!.DisplayName.Should().Be("Renamed Profile");
    }

    [TestMethod]
    public async Task DeleteRuntimeProfileAsync_True_Then_False_And_Blocks_When_Referenced()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.CreateRuntimeProfileAsync(NewProfileRequest("profile_del"));

        (await service.DeleteRuntimeProfileAsync("profile_del")).Should().BeTrue();
        (await service.DeleteRuntimeProfileAsync("profile_del")).Should().BeFalse();

        await service.CreateRuntimeProfileAsync(NewProfileRequest("profile_ref"));
        db.Models.Add(new Model
        {
            ModelId = "ref-model",
            DisplayName = "Ref",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"router","runtimeProfileId":"profile_ref"}""",
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var act = async () => await service.DeleteRuntimeProfileAsync("profile_ref");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*referenced by one or more models*");
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

    private static CreateRuntimeProfileRequest NewProfileRequest(string profileId) =>
        new(
            ProfileId: profileId,
            DisplayName: "Profile",
            Description: "desc",
            CombineSystemAndDeveloperMessages: false,
            ThoughtBlockPattern: null,
            SamplingParametersJson: "{}",
            ThinkingControlJson: "{}",
            Providers: null);

    private static UpdateRuntimeProfileRequest NewUpdateProfileRequest(string profileId) =>
        new(
            ProfileId: profileId,
            DisplayName: "Profile",
            Description: "desc",
            CombineSystemAndDeveloperMessages: false,
            ThoughtBlockPattern: null,
            SamplingParametersJson: "{}",
            ThinkingControlJson: "{}",
            Providers: null);

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
