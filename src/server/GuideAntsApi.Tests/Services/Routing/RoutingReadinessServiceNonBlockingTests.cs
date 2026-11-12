using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

/// <summary>T-U4 — NonBlocking issues zero HTTP yet still returns RuntimeState +
/// RuntimeObservedUtc from the seeded cache (shown, not hidden — F-2); config blockers
/// still present; Live output identical to the baseline tests.</summary>
[TestClass]
public sealed class RoutingReadinessServiceNonBlockingTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110",
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
            })
            .Build();

private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"nonblocking-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    // Mirrors RoutingReadinessServiceTests.CreateAppSettings exactly (the baseline suite's
    // construction), so NonBlocking and Live run against the same fixture shape.
    private static ApplicationSettingsService CreateAppSettings(ApplicationDbContext db, IConfiguration configuration)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(v => v.ContentRootPath).Returns(AppContext.BaseDirectory);

        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(v => v.CurrentValue).Returns(new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            }
        });
        return new ApplicationSettingsService(db, new SettingsSectionRegistry(), environment.Object, configuration, settingsSecrets.Object);
    }

    [TestMethod]
    public async Task NonBlocking_IssuesZeroHttp_YetShowsRuntimeState_FromCache()
    {
        using var db = CreateDb();
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));
        var configuration = Config();
        var appSettings = CreateAppSettings(db, configuration);

        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        // Seeded Plane C: endpoint reachable, runtime state 'unloaded', observed now.
        var cache = new LocalEndpointCache(new ThrowingProbe());
        cache.MarkObserved("http://localhost:8110/emb/ready", new EndpointObservation(
            "http://localhost:8110/emb/ready", EndpointAvailability.Reachable, DateTime.UtcNow, 4, null, 0, "unloaded"));

        var service = new RoutingReadinessService(
            appSettings, inventory.Object, new TestServiceScopeFactory(db, configuration),
            NullLogger<RoutingReadinessService>.Instance,
            loadService: null, stackClients: null,
            endpointCache: cache, inventoryMirror: null);

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", cancellationToken: default, probeMode: RoutingProbeMode.NonBlocking);

        result.RuntimeState.Should().Be("unloaded", "status shown is status known (F-2)");
        result.RuntimeObservedUtc.Should().NotBeNull();
        result.RuntimeObserved.Should().BeTrue();
    }

    [TestMethod]
    public async Task NonBlocking_NeverObserved_YieldsUnknown_NotABlockedRow()
    {
        using var db = CreateDb();
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));
        var configuration = Config();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings, inventory.Object, new TestServiceScopeFactory(db, configuration),
            NullLogger<RoutingReadinessService>.Instance);

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", cancellationToken: default, probeMode: RoutingProbeMode.NonBlocking);

        result.RuntimeState.Should().BeNull("never observed is a truthful absence, not a blocker");
        result.RuntimeObserved.Should().BeFalse();
        result.Status.Should().Be("ready", "Unknown yields no blocker on the NonBlocking path (L-2)");
    }

    [TestMethod]
    public async Task NonBlocking_UnreachableAux_EmitsEndpointUnreachable_NeverArtifactMissing()
    {
        using var db = CreateDb();
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));
        var configuration = Config();
        var appSettings = CreateAppSettings(db, configuration);

        // Persist a local selection so the aux branch has something to compare.
        await appSettings.SetServiceModeModelIdAsync(RoutedServiceNames.Embeddings, "bge-small");
        var modes = await appSettings.GetServiceModesAsync(RoutedServiceNames.Embeddings);
        modes.Should().NotBeEmpty();

        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var cache = new LocalEndpointCache(new ThrowingProbe());
        cache.MarkObserved("http://localhost:8110/emb/ready", new EndpointObservation(
            "http://localhost:8110/emb/ready", EndpointAvailability.Unreachable, DateTime.UtcNow, 4, "refused", 1));

        var service = new RoutingReadinessService(
            appSettings, inventory.Object, new TestServiceScopeFactory(db, configuration),
            NullLogger<RoutingReadinessService>.Instance,
            endpointCache: cache, inventoryMirror: null);

        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default", cancellationToken: default, probeMode: RoutingProbeMode.NonBlocking);

        // A down host must never masquerade as a deleted model (T-U7/F-4 boundary).
        result.Blockers.Should().NotContain(b => b.Contains("RUNTIME_ARTIFACT_MISSING"));
        result.Blockers.Should().Contain(b => b.StartsWith("ENDPOINT_UNREACHABLE:"));
    }

    [TestMethod]
    public async Task Live_BehavesAsBaseline_UnchangedByTheModeAddition()
    {
        using var db = CreateDb();
        // Same fixture as the baseline's first test (AzureOpenAiEmbedding, configured).
        SeedMode(db, RoutedServiceNames.Embeddings, new ServiceMode(
            ModeId: "default",
            ProviderSection: "AzureOpenAiEmbedding",
            ModelId: null,
            RequestPresetJson: null,
            Enabled: true,
            IsDefault: true));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAiEmbedding:Endpoint"] = "https://example.openai.azure.com",
                ["AzureOpenAiEmbedding:ApiKey"] = "test-key",
            })
            .Build();
        var appSettings = CreateAppSettings(db, configuration);
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var service = new RoutingReadinessService(
            appSettings, inventory.Object, new TestServiceScopeFactory(db, configuration),
            NullLogger<RoutingReadinessService>.Instance);

        // Live with no load service: the aux warmup branch returns empty (same as baseline
        // tests that construct without loadService). Blockers equal the baseline shape.
        var result = await service.ProbeModeAsync(RoutedServiceNames.Embeddings, "default");

        result.Status.Should().Be("ready");
        result.Blockers.Should().BeEmpty();
    }

    private static void SeedMode(ApplicationDbContext db, string serviceName, ServiceMode mode)
    {
        var row = db.ApplicationSettings.SingleOrDefault(x => x.SectionName == ServiceModeResolver.SectionName);
        System.Text.Json.Nodes.JsonObject payload;
        if (row == null)
        {
            payload = new System.Text.Json.Nodes.JsonObject();
            ServiceModesPayload.WriteModesFor(payload, serviceName, new[] { mode }, mode.ModeId);
            db.ApplicationSettings.Add(new ApplicationSetting
            {
                SectionName = ServiceModeResolver.SectionName,
                SchemaVersion = 1,
                JsonValue = payload.ToJsonString(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            payload = System.Text.Json.Nodes.JsonNode.Parse(row.JsonValue) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();
            var existing = ServiceModesPayload.ReadModesFor(payload, serviceName).ToList();
            existing.RemoveAll(m => m.ModeId == mode.ModeId);
            existing.Add(mode);
            ServiceModesPayload.WriteModesFor(
                payload,
                serviceName,
                existing,
                existing.FirstOrDefault(m => m.IsDefault)?.ModeId ?? existing.FirstOrDefault()?.ModeId);
            row.JsonValue = payload.ToJsonString();
        }

        db.SaveChanges();
    }


    private sealed class ThrowingProbe : ILocalEndpointProbe
    {
        public Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("NonBlocking must issue zero HTTP (T-U4)");
    }
}
