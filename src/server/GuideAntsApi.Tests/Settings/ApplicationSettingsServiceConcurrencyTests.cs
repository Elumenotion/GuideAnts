using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Settings;

/// <summary>
/// Regression lock for the rev3 plan §12 defect: P2's Task.WhenAll fan-out across the five
/// routed services ran GetServiceModesAsync (a scoped-DbContext read) in parallel on one
/// request-scoped ApplicationSettingsService, and EF's ConcurrencyDetector threw
/// "A second operation was started on this context instance..." (the 500 observed in
/// SettingsEndpointsDeepTests.Overview_And_ConnectionUsage_ReturnOk).
///
/// The read lane now opens its own short-lived factory context, so the P2 shape — and the
/// P3 toolbar concurrency it unblocks — must be exercised here, not reviewed away.
/// </summary>
[TestClass]
public sealed class ApplicationSettingsServiceConcurrencyTests
{
    [TestMethod]
    public async Task GetServiceModesAsync_FiveWayFanOut_P2Shape_DoesNotThrowAndReadsAreConsistent()
    {
        var (db, dbName) = CreateDbContext();
        await using var dbScope = db;
        SeedServiceModes(db, RoutedServiceNames.Embeddings, new[]
        {
            new ServiceMode("local", "LocalServiceHosts:EmbeddingsBaseUrl", "bge-small", null, true, true)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, dbName);

        // Exactly the shape SettingsOverviewEndpoints.cs:31-44 runs: one scoped service
        // instance, five parallel GetServiceModesAsync calls, one context per read lane.
        var rollups = await Task.WhenAll(RoutedServiceNames.All.Select(async serviceName =>
        {
            var modes = await service.GetServiceModesAsync(serviceName);
            return (Service: serviceName, Modes: modes);
        }));

        rollups.Should().HaveCount(RoutedServiceNames.All.Count);
        var embeddings = rollups.Single(r => r.Service == RoutedServiceNames.Embeddings);
        embeddings.Modes.Should().ContainSingle(m => m.ModeId == "local");
        embeddings.Modes.Single().ProviderSection.Should().Be("LocalServiceHosts:EmbeddingsBaseUrl");
        // The other four services have no modes; reads must still succeed (empty), not throw.
        rollups.Where(r => r.Service != RoutedServiceNames.Embeddings)
            .Should().OnlyContain(r => r.Modes.Count == 0);
    }

    [TestMethod]
    public async Task GetServiceModesAsync_FanOutConcurrentWithAWriteLane_ParallelReadsDoNotCorruptWriteContext()
    {
        // The write lane must keep the shared scoped context (change tracking) while reads
        // go through the factory: a write committed via the shared context must be visible
        // to a factory-backed read that ran concurrently with the write transaction setup.
        var (db, dbName) = CreateDbContext();
        await using var dbScope = db;
        // The write lane requires an existing default mode to persist into; the mode starts
        // with NO model id so the post-write assertion proves the write actually landed.
        SeedServiceModes(db, RoutedServiceNames.ImageGeneration, new[]
        {
            new ServiceMode("local", "LocalServiceHosts:ImageGenerationBaseUrl", null, null, true, true)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, dbName);

        var readTask = Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            service.GetServiceModesAsync(RoutedServiceNames.ImageGeneration)));
        await service.SetServiceModeModelIdAsync(RoutedServiceNames.ImageGeneration, "sdxl-turbo");
        var readResults = await readTask;

        readResults.Should().HaveCount(5);
        // Every parallel read saw either the pre-write or the post-write row, never threw,
        // and the write lane (shared context) is untouched by the read lane's own contexts.
        var after = await service.GetServiceModesAsync(RoutedServiceNames.ImageGeneration);
        after.Should().Contain(m => m.ModeId == "local" && m.ModelId == "sdxl-turbo");
    }

    // (ApplicationDbContext, databaseName) together: the suite runs with method-level
    // parallelization (MSTestSettings.cs), so the per-test database name must never live
    // in a shared field — a concurrent test method could overwrite it between
    // CreateDbContext and CreateService, and the factory would then point at the other
    // test's database.
    private static (ApplicationDbContext Db, string Name) CreateDbContext()
    {
        var name = $"settings-concurrency-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return (new ApplicationDbContext(options), name);
    }

    private static ApplicationSettingsService CreateService(ApplicationDbContext db, string inMemoryDbName)
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
            db, new SettingsSectionRegistry(), environment.Object,
            new ConfigurationBuilder().Build(), settingsSecrets.Object,
            dbFactory: TestDbContextFactory.FromInMemory(inMemoryDbName));
    }


    private static void SeedServiceModes(ApplicationDbContext db, string serviceName, ServiceMode[] modes)
    {
        var row = db.ApplicationSettings.SingleOrDefault(x => x.SectionName == ServiceModeResolver.SectionName);
        JsonObject payload = row == null ? new() : ApplicationSettingsJson.DeserializeObject(row.JsonValue);
        ServiceModesPayload.WriteModesFor(payload, serviceName, modes, modes.First().ModeId);
        if (row == null)
        {
            db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
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
            row.JsonValue = payload.ToJsonString();
            row.UpdatedUtc = DateTime.UtcNow;
        }
    }
}
