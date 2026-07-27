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
using System.Text.Json.Nodes;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsServiceDeepTests
{
    private const string TelemetrySection = "Telemetry";

    [TestMethod]
    public async Task GetSectionAsync_Returns_null_for_unknown_section()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        (await service.GetSectionAsync("DoesNotExist")).Should().BeNull();
    }

    [TestMethod]
    public async Task GetSectionAsync_Returns_section_after_bootstrap()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        var section = await service.GetSectionAsync(TelemetrySection);

        section.Should().NotBeNull();
        section!.SectionName.Should().Be(TelemetrySection);
    }

    [TestMethod]
    public async Task GetSectionSummariesAsync_Returns_all_registered_sections()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        var summaries = await service.GetSectionSummariesAsync();

        summaries.Should().NotBeEmpty();
        summaries.Should().Contain(s => s.SectionName == TelemetrySection);
    }

    [TestMethod]
    public async Task GetSchemaAsync_Returns_sections_services_and_providers()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        var schema = await service.GetSchemaAsync();

        schema.Sections.Should().NotBeEmpty();
        schema.Services.Should().NotBeEmpty();
        schema.Providers.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Returns_error_for_unsupported_section()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        var result = await service.UpdateSectionAsync(
            "TotallyMadeUp",
            new UpdateSettingsSectionRequest("AA==", new JsonObject()));

        result.Section.Should().BeNull();
        result.ConcurrencyConflict.Should().BeFalse();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("Unsupported section", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Returns_not_found_for_unseeded_runtime_section()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        // LlamaCpp is a runtime-override section that bootstrap does NOT seed.
        var result = await service.UpdateSectionAsync(
            "LlamaCpp",
            new UpdateSettingsSectionRequest("AA==", new JsonObject()));

        result.Section.Should().BeNull();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("not found", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Detects_concurrency_conflict_on_stale_rowversion()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());

        var result = await service.UpdateSectionAsync(
            TelemetrySection,
            new UpdateSettingsSectionRequest("ZZZZ-stale-rowversion", new JsonObject
            {
                ["Default"] = "Information"
            }));

        result.ConcurrencyConflict.Should().BeTrue();
        result.Section.Should().BeNull();
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Rejects_unsupported_field()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());
        var section = await service.GetSectionAsync(TelemetrySection);

        var result = await service.UpdateSectionAsync(
            TelemetrySection,
            new UpdateSettingsSectionRequest(section!.RowVersion, new JsonObject
            {
                ["NotARealField"] = "x"
            }));

        result.Section.Should().BeNull();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("is not supported by section", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Rejects_invalid_telemetry_log_level()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());
        var section = await service.GetSectionAsync(TelemetrySection);

        var result = await service.UpdateSectionAsync(
            TelemetrySection,
            new UpdateSettingsSectionRequest(section!.RowVersion, new JsonObject
            {
                ["Default"] = "Bogus"
            }));

        result.Section.Should().BeNull();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("invalid log level", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Rejects_blank_telemetry_log_level()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());
        var section = await service.GetSectionAsync(TelemetrySection);

        var result = await service.UpdateSectionAsync(
            TelemetrySection,
            new UpdateSettingsSectionRequest(section!.RowVersion, new JsonObject
            {
                ["Default"] = ""
            }));

        result.Section.Should().BeNull();
        result.ValidationErrors.Should().ContainSingle(e => e.Contains("must be one of", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Persists_valid_telemetry_update()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, BuildConfiguration());
        await service.BootstrapAsync(BuildConfiguration());
        var section = await service.GetSectionAsync(TelemetrySection);

        var result = await service.UpdateSectionAsync(
            TelemetrySection,
            new UpdateSettingsSectionRequest(section!.RowVersion, new JsonObject
            {
                ["Default"] = "Information"
            }));

        result.ConcurrencyConflict.Should().BeFalse();
        result.ValidationErrors.Should().BeEmpty();
        result.Section.Should().NotBeNull();
        result.Section!.Payload["Default"]!.GetValue<string>().Should().Be("Information");

        var reloaded = await service.GetSectionAsync(TelemetrySection);
        reloaded!.Payload["Default"]!.GetValue<string>().Should().Be("Information");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"settings-deep-{Guid.NewGuid():N}")
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
