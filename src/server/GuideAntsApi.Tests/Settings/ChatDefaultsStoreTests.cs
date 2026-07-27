using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ChatDefaultsStoreTests
{
    [TestMethod]
    public async Task UpdateSectionAsync_IsVisibleToStoreCurrentImmediately()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IChatDefaultsStore, ChatDefaultsStore>();
        services.AddSingleton(sp =>
            CreateSettingsService(
                db,
                configuration,
                sp.GetRequiredService<IChatDefaultsStore>()));
        services.AddSingleton<IApplicationSettingsService>(sp =>
            sp.GetRequiredService<ApplicationSettingsService>());

        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IApplicationSettingsService>();
        var store = provider.GetRequiredService<IChatDefaultsStore>();

        await settings.BootstrapAsync(configuration);
        var section = await settings.GetSectionAsync("ChatDefaults");
        section.Should().NotBeNull();

        store.Current.DefaultModelId.Should().BeNull();

        var result = await settings.UpdateSectionAsync(
            "ChatDefaults",
            new UpdateSettingsSectionRequest(
                section!.RowVersion,
                new JsonObject
                {
                    ["DefaultModelId"] = "gemini-2.5-flash",
                    ["OverrideAllChatModels"] = true
                }));

        result.Section.Should().NotBeNull();
        store.Current.DefaultModelId.Should().Be("gemini-2.5-flash");
        store.Current.OverrideAllChatModels.Should().BeTrue();
    }

    [TestMethod]
    public async Task Current_ReadsDatabaseWithoutPriorRefresh_AcrossStoreInstances()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration();

        var writerServices = new ServiceCollection();
        writerServices.AddSingleton(db);
        writerServices.AddSingleton<IConfiguration>(configuration);
        writerServices.AddSingleton<IChatDefaultsStore, ChatDefaultsStore>();
        writerServices.AddSingleton(sp =>
            CreateSettingsService(
                db,
                configuration,
                sp.GetRequiredService<IChatDefaultsStore>()));
        writerServices.AddSingleton<IApplicationSettingsService>(sp =>
            sp.GetRequiredService<ApplicationSettingsService>());

        await using var writerProvider = writerServices.BuildServiceProvider();
        var settings = writerProvider.GetRequiredService<IApplicationSettingsService>();

        await settings.BootstrapAsync(configuration);
        var section = await settings.GetSectionAsync("ChatDefaults");
        section.Should().NotBeNull();

        await settings.UpdateSectionAsync(
            "ChatDefaults",
            new UpdateSettingsSectionRequest(
                section!.RowVersion,
                new JsonObject
                {
                    ["DefaultModelId"] = "gemini-2.5-flash",
                    ["OverrideAllChatModels"] = false
                }));

        // Simulate a second API replica: a brand-new store that never received RefreshAsync
        // from the write path must still see the persisted default.
        var readerServices = new ServiceCollection();
        readerServices.AddSingleton(db);
        readerServices.AddSingleton<IConfiguration>(configuration);
        readerServices.AddSingleton(sp => CreateSettingsService(db, configuration));
        readerServices.AddSingleton<IApplicationSettingsService>(sp =>
            sp.GetRequiredService<ApplicationSettingsService>());
        readerServices.AddSingleton<IChatDefaultsStore, ChatDefaultsStore>();

        await using var readerProvider = readerServices.BuildServiceProvider();
        var otherReplicaStore = readerProvider.GetRequiredService<IChatDefaultsStore>();

        otherReplicaStore.Current.DefaultModelId.Should().Be("gemini-2.5-flash");
        otherReplicaStore.Current.OverrideAllChatModels.Should().BeFalse();
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"chat-defaults-store-{Guid.NewGuid():N}")
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

    private static ApplicationSettingsService CreateSettingsService(
        ApplicationDbContext db,
        IConfiguration configuration,
        IChatDefaultsStore? chatDefaultsStore = null)
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

        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object,
            new Mock<IRuntimeProfileResolver>().Object,
            chatDefaultsStore: chatDefaultsStore);
    }
}
