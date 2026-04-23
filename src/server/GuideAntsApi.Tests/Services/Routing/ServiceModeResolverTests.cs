using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ServiceModeResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_ReturnsEnabledDefault_WhenModeIdOmitted()
    {
        using var db = CreateDb();
        SeedServiceModes(db, "Embeddings", defaultModeId: "default", modes:
        [
            new ServiceMode("default", "AzureOpenAiEmbedding", ModelId: null, RequestPresetJson: null, Enabled: true, IsDefault: true),
            new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", ModelId: null, RequestPresetJson: null, Enabled: true, IsDefault: false)
        ]);

        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));
        var mode = await resolver.ResolveAsync(RoutedServiceNames.Embeddings, modeId: null);

        mode.ModeId.Should().Be("default");
        mode.ProviderSection.Should().Be("AzureOpenAiEmbedding");
        mode.IsDefault.Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveAsync_ReturnsMatchingMode_WhenModeIdProvided()
    {
        using var db = CreateDb();
        SeedServiceModes(db, "ImageGeneration", defaultModeId: "default", modes:
        [
            new ServiceMode("default", "AzureOpenAiImages", null, null, Enabled: true, IsDefault: true),
            new ServiceMode("sd-local", "LocalServiceHosts:ImageGenerationBaseUrl", null, null, Enabled: true, IsDefault: false)
        ]);

        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));
        var mode = await resolver.ResolveAsync(RoutedServiceNames.ImageGeneration, modeId: "sd-local");

        mode.ModeId.Should().Be("sd-local");
        mode.ProviderSection.Should().Be("LocalServiceHosts:ImageGenerationBaseUrl");
    }

    [TestMethod]
    public async Task ResolveAsync_Throws_ModeNotFound_WhenServiceHasNoModes()
    {
        using var db = CreateDb();
        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));

        Func<Task> act = async () => await resolver.ResolveAsync(RoutedServiceNames.SpeechSynthesis, modeId: null);
        await act.Should().ThrowAsync<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModeNotFound
                         && ex.ServiceId == RoutedServiceNames.SpeechSynthesis);
    }

    [TestMethod]
    public async Task ResolveAsync_Throws_ModeNotFound_WhenSpecificModeMissing()
    {
        using var db = CreateDb();
        SeedServiceModes(db, "SpeechTranscription", defaultModeId: "default", modes:
        [
            new ServiceMode("default", "AzureSpeechService", null, null, Enabled: true, IsDefault: true)
        ]);

        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));

        Func<Task> act = async () => await resolver.ResolveAsync(RoutedServiceNames.SpeechTranscription, modeId: "does-not-exist");
        await act.Should().ThrowAsync<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModeNotFound
                         && ex.ModeId == "does-not-exist");
    }

    [TestMethod]
    public async Task ResolveAsync_Throws_ModeNotFound_WhenSelectedModeIsDisabled()
    {
        using var db = CreateDb();
        SeedServiceModes(db, "DocumentIntelligence", defaultModeId: "default", modes:
        [
            new ServiceMode("default", "AzureDocumentIntelligence", null, null, Enabled: false, IsDefault: true)
        ]);

        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));

        Func<Task> act = async () => await resolver.ResolveAsync(RoutedServiceNames.DocumentIntelligence, modeId: "default");
        await act.Should().ThrowAsync<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModeNotFound);
    }

    [TestMethod]
    public async Task ResolveAsync_Throws_WhenDefaultIsDisabled()
    {
        using var db = CreateDb();
        SeedServiceModes(db, "Embeddings", defaultModeId: "default", modes:
        [
            new ServiceMode("default", "AzureOpenAiEmbedding", null, null, Enabled: false, IsDefault: true),
            new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", null, null, Enabled: true, IsDefault: false)
        ]);

        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));

        Func<Task> act = async () => await resolver.ResolveAsync(RoutedServiceNames.Embeddings, modeId: null);
        await act.Should().ThrowAsync<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModeNotFound);
    }

    [TestMethod]
    public async Task GetModesAsync_ReturnsEmpty_WhenRowMissing()
    {
        using var db = CreateDb();
        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));

        var modes = await resolver.GetModesAsync(RoutedServiceNames.Embeddings);
        modes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetModesAsync_RejectsUnknownServiceName()
    {
        using var db = CreateDb();
        var resolver = new ServiceModeResolver(new TestServiceScopeFactory(db));

        Func<Task> act = async () => await resolver.GetModesAsync("Chat");
        await act.Should().ThrowAsync<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModeNotFound);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"service-mode-resolver-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static void SeedServiceModes(
        ApplicationDbContext db,
        string serviceName,
        string? defaultModeId,
        IReadOnlyList<ServiceMode> modes)
    {
        var payload = new JsonObject();
        ServiceModesPayload.WriteModesFor(payload, serviceName, modes, defaultModeId);

        db.ApplicationSettings.Add(new ApplicationSetting
        {
            SectionName = ServiceModeResolver.SectionName,
            JsonValue = payload.ToJsonString(),
            SchemaVersion = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}
