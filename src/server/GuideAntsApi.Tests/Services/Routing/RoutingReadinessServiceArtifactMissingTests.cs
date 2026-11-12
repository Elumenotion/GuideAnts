using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

/// <summary>T-U7 — the three aux outcomes are distinguishable, and the F-4 boundary holds:
/// reachable + selection absent + install row exists → RUNTIME_ARTIFACT_MISSING;
/// unreachable → ENDPOINT_UNREACHABLE and NEVER an artifact blocker (a down host must not
/// masquerade as a deleted model); loaded → clean; no install row → no artifact claim.</summary>
[TestClass]
public sealed class RoutingReadinessServiceArtifactMissingTests
{
    private const string ReadyUrl = "http://localhost:8110/emb/ready";

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            })
            .Build();

private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"artifact-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static (ApplicationSettingsService Settings, ServiceLocalModelInstallationStore Store) CreateServices(ApplicationDbContext db, IConfiguration configuration)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(v => v.ContentRootPath).Returns(AppContext.BaseDirectory);
        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(v => v.CurrentValue).Returns(new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal) { ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=" },
        });
        var settings = new ApplicationSettingsService(db, new SettingsSectionRegistry(), environment.Object, configuration, settingsSecrets.Object);

        var dbName = $"artifact-store-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IServiceLocalModelInstallationStore, ServiceLocalModelInstallationStore>();
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IServiceLocalModelInstallationStore>();
        return (settings, (ServiceLocalModelInstallationStore)store);
    }

    private static void SeedCatalogRow(ApplicationDbContext db, string modelId)
    {
        db.Models.Add(new GuideAntsApi.DataModel.Models.Model
        {
            ModelId = modelId,
            Provider = "openai-compatible",
            DisplayName = modelId,
            Description = null,
            ReasoningChoicesJson = null,
            RuntimeConfigJson = null,
            IsActive = true,
            DisplayOrder = 1,
            Created = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static void SeedMode(ApplicationDbContext db)
    {
        var row = db.ApplicationSettings.SingleOrDefault(x => x.SectionName == ServiceModeResolver.SectionName);
        System.Text.Json.Nodes.JsonObject payload = new();
        ServiceModesPayload.WriteModesFor(payload, RoutedServiceNames.Embeddings, new[]
        {
            new ServiceMode("default", "LocalServiceHosts:EmbeddingsBaseUrl", "bge-small", null, true, true),
        }, "default");
        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = payload.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static RoutingReadinessService CreateService(
        ApplicationDbContext db,
        ApplicationSettingsService settings,
        IConfiguration configuration,
        ILocalEndpointCache cache,
        ILocalInventoryMirror mirror,
        IServiceLocalModelInstallationStore store)
    {
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());
        return new RoutingReadinessService(
            settings, inventory.Object, new TestServiceScopeFactory(db, configuration),
            NullLogger<RoutingReadinessService>.Instance,
            endpointCache: cache, inventoryMirror: mirror, installStore: store);
    }

    [TestMethod]
    public async Task Reachable_SelectionAbsent_InstallRowExists_EmitsRuntimeArtifactMissing()
    {
        using var db = CreateDb();
        SeedMode(db);
        SeedCatalogRow(db, "bge-small");
        var configuration = Config();
        var (settings, store) = CreateServices(db, configuration);
        await store.UpsertAsync(RoutedServiceNames.Embeddings, "bge-small", "model", "bge-small", null, null, "Installed");

        var cache = new LocalEndpointCache(new ThrowingProbe());
        cache.MarkObserved(ReadyUrl, new EndpointObservation(ReadyUrl, EndpointAvailability.Reachable, DateTime.UtcNow, 4, null, 0, "unloaded"));
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.Embeddings,
            [new GuideAntsApi.Models.NotebookToolbarLocalModelOptionDto("other-model", "other-model", true, false)], DateTime.UtcNow);

        var service = CreateService(db, settings, configuration, cache, mirror, store);
        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", probeMode: RoutingProbeMode.NonBlocking);

        result.Blockers.Should().Contain(b => b.Contains("RUNTIME_ARTIFACT_MISSING")).And.HaveCount(1);
    }

    [TestMethod]
    public async Task Unreachable_NeverEmitsArtifactBlocker_EmitsEndpointUnreachable()
    {
        using var db = CreateDb();
        SeedMode(db);
        var configuration = Config();
        var (settings, store) = CreateServices(db, configuration);
        await store.UpsertAsync(RoutedServiceNames.Embeddings, "bge-small", "model", "bge-small", null, null, "Installed");

        var cache = new LocalEndpointCache(new ThrowingProbe());
        cache.MarkObserved(ReadyUrl, new EndpointObservation(ReadyUrl, EndpointAvailability.Unreachable, DateTime.UtcNow, 4, "refused", 1));
        var mirror = new LocalInventoryMirror();

        var service = CreateService(db, settings, configuration, cache, mirror, store);
        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", probeMode: RoutingProbeMode.NonBlocking);

        result.Blockers.Should().NotContain(b => b.Contains("RUNTIME_ARTIFACT_MISSING"),
            "a down host must never masquerade as a deleted model");
        result.Blockers.Should().Contain(b => b.StartsWith("ENDPOINT_UNREACHABLE:"));
    }

    [TestMethod]
    public async Task Reachable_AndLoaded_IsClean()
    {
        using var db = CreateDb();
        SeedMode(db);
        SeedCatalogRow(db, "bge-small");
        var configuration = Config();
        var (settings, store) = CreateServices(db, configuration);
        await store.UpsertAsync(RoutedServiceNames.Embeddings, "bge-small", "model", "bge-small", null, null, "Installed");

        var cache = new LocalEndpointCache(new ThrowingProbe());
        cache.MarkObserved(ReadyUrl, new EndpointObservation(ReadyUrl, EndpointAvailability.Reachable, DateTime.UtcNow, 4, null, 0, "loaded"));
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.Embeddings,
            [new GuideAntsApi.Models.NotebookToolbarLocalModelOptionDto("bge-small", "bge-small", true, true)], DateTime.UtcNow);

        var service = CreateService(db, settings, configuration, cache, mirror, store);
        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", probeMode: RoutingProbeMode.NonBlocking);

        result.Blockers.Should().BeEmpty();
        result.Status.Should().Be("ready");
    }

    [TestMethod]
    public async Task NoInstallRow_EmitsNoArtifactClaim_ProvenanceUnknownIsLegitimate()
    {
        using var db = CreateDb();
        SeedMode(db);
        SeedCatalogRow(db, "bge-small");
        var configuration = Config();
        var (settings, store) = CreateServices(db, configuration); // no row inserted

        var cache = new LocalEndpointCache(new ThrowingProbe());
        cache.MarkObserved(ReadyUrl, new EndpointObservation(ReadyUrl, EndpointAvailability.Reachable, DateTime.UtcNow, 4, null, 0, "unloaded"));
        var mirror = new LocalInventoryMirror();
        mirror.SetLive(RoutedServiceNames.Embeddings,
            [new GuideAntsApi.Models.NotebookToolbarLocalModelOptionDto("other-model", "other-model", true, false)], DateTime.UtcNow);

        var service = CreateService(db, settings, configuration, cache, mirror, store);
        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", probeMode: RoutingProbeMode.NonBlocking);

        result.Blockers.Should().NotContain(b => b.Contains("RUNTIME_ARTIFACT_MISSING"),
            "pre-migration provenance unknown renders as verify-or-remove, never an artifact claim");
    }

    [TestMethod]
    public async Task EndpoointUnreachable_IsNeverEmittedOnTheLlamaChatBranch()
    {
        // F-4 boundary (chat side): a llama-cpp chat target under NonBlocking must not
        // produce ENDPOINT_UNREACHABLE — it stays whitelist-eligible via cache state only.
        using var db = CreateDb();
        var configuration = new ConfigurationBuilder().Build();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(v => v.ContentRootPath).Returns(AppContext.BaseDirectory);
        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(v => v.CurrentValue).Returns(new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal) { ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=" },
        });
        var settings = new ApplicationSettingsService(db, new SettingsSectionRegistry(), environment.Object, configuration, settingsSecrets.Object);

        // Seed a llama-cpp catalog model.
        db.Models.Add(new GuideAntsApi.DataModel.Models.Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            DisplayName = "Qwen Local",
            Description = null,
            ReasoningChoicesJson = null,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-local\"}",
            IsActive = true,
            DisplayOrder = 1,
            Created = DateTime.UtcNow,
        });
        db.SaveChanges();

        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var cache = new LocalEndpointCache(new ThrowingProbe());
        var mirror = new LocalInventoryMirror();
        mirror.SetLive("llama",
            [new GuideAntsApi.Models.NotebookToolbarLocalModelOptionDto("qwen-local", "qwen-local", true, false)], DateTime.UtcNow);

        var service = new RoutingReadinessService(
            settings, inventory.Object, new TestServiceScopeFactory(db, configuration),
            NullLogger<RoutingReadinessService>.Instance,
            endpointCache: cache, inventoryMirror: mirror);

        var result = await service.ProbeChatTargetAsync("qwen-local", cancellationToken: default, "direct", RoutingProbeMode.NonBlocking);

        result.Blockers.Should().NotContain(b => b.StartsWith("ENDPOINT_UNREACHABLE:"),
            "a non-whitelisted blocker on a chat row would hide it from the selectable list (F-4)");
        result.RuntimeState.Should().Be("unloaded", "shown from the cache, not hidden");
    }

    private sealed class ThrowingProbe : ILocalEndpointProbe
    {
        public Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("NonBlocking must issue zero HTTP (T-U7)");
    }
}
