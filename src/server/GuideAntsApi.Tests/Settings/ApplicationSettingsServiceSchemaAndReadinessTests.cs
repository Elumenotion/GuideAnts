using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsServiceSchemaAndReadinessTests
{
    [TestMethod]
    public async Task GetSchemaAsync_ReturnsServiceProviderGraphAndRuntimeDependencies()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var service = CreateService(db, configuration);

        var schema = await service.GetSchemaAsync();

        schema.Services.Should().NotBeEmpty();
        schema.Providers.Should().NotBeEmpty();
        schema.RuntimeDependencies.Should().NotBeEmpty();
        schema.RuntimeDependencies.Should().OnlyContain(dependency => dependency.ReadOnly);
        schema.Sections.Should().NotContain(section => string.Equals(section.SectionName, "Search", StringComparison.Ordinal));
        schema.Sections.Should().Contain(section =>
            string.Equals(section.SectionName, "ChatDefaults", StringComparison.Ordinal));
        schema.Sections.Should().Contain(section =>
            string.Equals(section.SectionName, SettingsSectionRegistry.TelemetrySectionName, StringComparison.Ordinal)
            && section.Properties.Any(property => property.Name == "GuideAntsApiBackgroundJobs")
            && section.Properties.All(property => property.ValueType == "string"));

        schema.Services.Should().OnlyContain(serviceDefinition =>
            serviceDefinition.ProviderIds.All(providerId =>
                schema.Providers.Any(provider =>
                    string.Equals(provider.ProviderId, providerId, StringComparison.Ordinal)
                    && string.Equals(provider.ServiceId, serviceDefinition.ServiceId, StringComparison.Ordinal))));

        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp, StringComparison.Ordinal)
            && string.Equals(provider.RuntimeSectionName, LocalServiceHostsOptions.SectionName, StringComparison.Ordinal)
            && provider.RequiredRuntimeKeys.Any(key => string.Equals(
                key.Key,
                "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                StringComparison.Ordinal)));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.EmbeddingsGoogleEmbedding, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, GoogleGeminiApiOptions.SectionName, StringComparison.Ordinal));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.EmbeddingsOpenRouterEmbeddings, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, OpenRouterOptions.SectionName, StringComparison.Ordinal));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.EmbeddingsHuggingFaceInference, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, "HuggingFace", StringComparison.Ordinal));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.SpeechTranscriptionLocalAsrHttp, StringComparison.Ordinal)
            && provider.RequiredRuntimeKeys.Any(key => string.Equals(
                key.Key,
                "LocalServiceHosts:MediaBaseUrl",
                StringComparison.Ordinal)));

        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.SpeechTranscriptionOpenAiAudio, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, "OpenAI", StringComparison.Ordinal));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.SpeechSynthesisOpenAiTts, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, "OpenAI", StringComparison.Ordinal));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.ImageGenerationOpenAiImages, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, "OpenAI", StringComparison.Ordinal));
        schema.Providers.Should().Contain(provider =>
            string.Equals(provider.ProviderId, ServiceProviderIds.EmbeddingsOpenAiEmbedding, StringComparison.Ordinal)
            && string.Equals(provider.ProviderSettingsSection, "OpenAI", StringComparison.Ordinal));

        schema.Sections.Should().Contain(section =>
            string.Equals(section.SectionName, GoogleGeminiApiOptions.SectionName, StringComparison.Ordinal)
            && section.Properties.Any(property => property.Name == "ApiKey" && property.IsRequired)
            && section.Properties.Count == 1);
        schema.Sections.Should().Contain(section =>
            string.Equals(section.SectionName, OpenRouterOptions.SectionName, StringComparison.Ordinal)
            && section.Properties.Any(property => property.Name == "ApiKey" && property.IsRequired));
        schema.Sections.Should().Contain(section =>
            string.Equals(section.SectionName, "HuggingFace", StringComparison.Ordinal)
            && section.Properties.Any(property => property.Name == "Token" && property.IsRequired));
        schema.Sections.Should().Contain(section =>
            string.Equals(section.SectionName, "OpenAI", StringComparison.Ordinal)
            && section.Properties.Any(property => property.Name == "ApiKey" && property.IsRequired));
    }

    [TestMethod]
    public async Task BootstrapAsync_CreatesTelemetrySection_WithConservativeDefaults()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Error",
            ["Logging:LogLevel:GuideAntsApi.BackgroundJobs"] = "Error"
        });
        var service = CreateService(db, configuration);

        await service.BootstrapAsync(configuration);

        var row = await db.ApplicationSettings
            .AsNoTracking()
            .SingleAsync(x => x.SectionName == SettingsSectionRegistry.TelemetrySectionName);
        var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);

        payload["Default"]!.GetValue<string>().Should().Be("Warning");
        payload["GuideAntsApiBackgroundJobs"]!.GetValue<string>().Should().Be("Information");
        payload["AntRunnerChat"]!.GetValue<string>().Should().Be("Warning");
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Telemetry_RejectsInvalidLogLevel()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var service = CreateService(db, configuration);

        var section = await service.GetSectionAsync(SettingsSectionRegistry.TelemetrySectionName);
        section.Should().NotBeNull();

        var result = await service.UpdateSectionAsync(
            SettingsSectionRegistry.TelemetrySectionName,
            new UpdateSettingsSectionRequest(
                section!.RowVersion,
                new JsonObject
                {
                    ["GuideAntsApiBackgroundJobs"] = "Chatty"
                }));

        result.ValidationErrors.Should().Contain(error =>
            error.Contains("GuideAntsApiBackgroundJobs", StringComparison.Ordinal)
            && error.Contains("Chatty", StringComparison.Ordinal));
        result.ConcurrencyConflict.Should().BeFalse();
        result.Section.Should().BeNull();
    }

    [TestMethod]
    public async Task UpdateSectionAsync_Telemetry_ReturnsConcurrencyConflict_ForStaleRowVersion()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var service = CreateService(db, configuration);

        await service.GetSectionAsync(SettingsSectionRegistry.TelemetrySectionName);

        var result = await service.UpdateSectionAsync(
            SettingsSectionRegistry.TelemetrySectionName,
            new UpdateSettingsSectionRequest(
                "stale-row-version",
                new JsonObject
                {
                    ["GuideAntsApiBackgroundJobs"] = "Debug"
                }));

        result.ConcurrencyConflict.Should().BeTrue();
        result.ValidationErrors.Should().BeEmpty();
        result.Section.Should().BeNull();
    }

    [TestMethod]
    public async Task GetSectionSummariesAsync_ReportsUnconfiguredGoogleGeminiProviderSection()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GoogleGeminiApi:ApiKey"] = null,
        });
        var service = CreateService(db, configuration);

        var summaries = await service.GetSectionSummariesAsync();
        var google = summaries.Single(section => string.Equals(section.SectionName, GoogleGeminiApiOptions.SectionName, StringComparison.Ordinal));

        google.ReadinessStatus.Should().Be("unconfigured");
        google.MissingFields.Should().ContainSingle().Which.Should().Be("ApiKey");
    }

    [TestMethod]
    public async Task GetSectionSummariesAsync_ReportsUnconfiguredOpenAiProviderSection()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = null,
        });
        var service = CreateService(db, configuration);

        var summaries = await service.GetSectionSummariesAsync();
        var openai = summaries.Single(section => string.Equals(section.SectionName, "OpenAI", StringComparison.Ordinal));

        openai.ReadinessStatus.Should().Be("unconfigured");
        openai.MissingFields.Should().ContainSingle().Which.Should().Be("ApiKey");
    }

    [TestMethod]
    public async Task GetSectionSummariesAsync_TreatsAzureDocumentIntelligencePlaceholdersAsUnconfigured()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AzureDocumentIntelligence:Endpoint"] = "https://example.cognitiveservices.azure.com/",
            ["AzureDocumentIntelligence:ApiKey"] = "replace-with-azure-document-intelligence-api-key",
        });
        var service = CreateService(db, configuration);

        var summaries = await service.GetSectionSummariesAsync();
        var docIntel = summaries.Single(section => string.Equals(section.SectionName, "AzureDocumentIntelligence", StringComparison.Ordinal));

        docIntel.ReadinessStatus.Should().Be("unconfigured");
        docIntel.MissingFields.Should().BeEquivalentTo(["Endpoint", "ApiKey"]);
    }

    [TestMethod]
    public async Task BootstrapAsync_DoesNotRecreateLlamaCppApplicationSettingsRow()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = "LlamaCpp",
            JsonValue = """{"BaseUrl":"http://db.example/llama-cpp"}""",
            SchemaVersion = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, configuration);

        await service.GetSectionSummariesAsync();

        db.ApplicationSettings.Should().NotContain(row =>
            string.Equals(row.SectionName, "LlamaCpp", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BootstrapAsync_ScrubsRetiredFields_FromFlatSectionRows()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = "SpeechTranscription",
            JsonValue = """{"TimeoutSeconds":300,"ActiveProviderId":"SpeechTranscription.AzureSpeech.Batch"}""",
            SchemaVersion = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, configuration);

        await service.BootstrapAsync(configuration);

        var row = await db.ApplicationSettings
            .AsNoTracking()
            .SingleAsync(x => x.SectionName == "SpeechTranscription");
        var payload = ApplicationSettingsJson.DeserializeObject(row.JsonValue);

        payload.ContainsKey("TimeoutSeconds").Should().BeTrue();
        payload["TimeoutSeconds"]!.GetValue<int>().Should().Be(300);
        payload.ContainsKey("ActiveProviderId").Should().BeFalse();
    }

    [TestMethod]
    public async Task GetReadinessAsync_ParityMatchesStartupValidatorEvaluate()
    {
        // Phase H / R-4.4: per-service ActiveProviderId validation is retired.
        // The only per-service errors the readiness surface exposes are the
        // ones the slimmed-down startup validator still emits (encrypted
        // secrets, gateway prefixes). Assert parity only.
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AzureSpeechService:ApiKey"] = "enc::not-decrypted"
        });
        var service = CreateService(db, configuration);

        var expectedErrors = ServiceRoutingStartupValidator.Evaluate(configuration);
        var readiness = await service.GetReadinessAsync();
        var readinessErrors = readiness.Services
            .SelectMany(serviceReadiness => serviceReadiness.Blockers)
            .Concat(readiness.GlobalBlockers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        readinessErrors.Should().BeEquivalentTo(expectedErrors);
    }

    [TestMethod]
    public async Task GetReadinessAsync_DoesNotReadActiveProviderId_WhenServiceModesExists()
    {
        // R-4.4 regression guard: even if a caller plants a bogus
        // {Service}:ActiveProviderId into configuration, readiness MUST NOT
        // surface it as a blocker.
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["SpeechTranscription:ActiveProviderId"] = "totally-bogus-value",
            ["Embeddings:ActiveProviderId"] = "also-garbage"
        });
        var service = CreateService(db, configuration);

        var readiness = await service.GetReadinessAsync();

        readiness.Services.Should().OnlyContain(serviceReadiness =>
            string.Equals(serviceReadiness.Status, "ready", StringComparison.Ordinal));
        readiness.GlobalBlockers.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BootstrapAsync_NormalizesSingleDefaultMode_WithoutAddingCounterpartModes()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        var preSeeded = new System.Text.Json.Nodes.JsonObject();
        foreach (var (serviceName, legacyProviderId) in new[]
        {
            (RoutedServiceNames.Embeddings, ServiceProviderIds.EmbeddingsLocalEmbHttp),
            (RoutedServiceNames.ImageGeneration, ServiceProviderIds.ImageGenerationLocalSdHttp),
            (RoutedServiceNames.SpeechTranscription, ServiceProviderIds.SpeechTranscriptionLocalAsrHttp),
            (RoutedServiceNames.SpeechSynthesis, ServiceProviderIds.SpeechSynthesisLocalTtsHttp),
            (RoutedServiceNames.DocumentIntelligence, ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp)
        })
        {
            ServiceModesPayload.WriteModesFor(
                preSeeded,
                serviceName,
                new[]
                {
                    new ServiceMode(
                        ModeId: "default",
                        ProviderSection: legacyProviderId,
                        ModelId: null,
                        RequestPresetJson: null,
                        Enabled: true,
                        IsDefault: true)
                },
                defaultModeId: "default");
        }

        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = preSeeded.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, configuration);
        await service.BootstrapAsync(configuration);

        foreach (var (serviceName, expectedLocalProviderSection, _) in new[]
        {
            (RoutedServiceNames.Embeddings, "LocalServiceHosts:EmbeddingsBaseUrl", "AzureOpenAiEmbedding"),
            (RoutedServiceNames.ImageGeneration, "LocalServiceHosts:ImageGenerationBaseUrl", "AzureOpenAiImages"),
            (RoutedServiceNames.SpeechTranscription, "LocalServiceHosts:SpeechTranscriptionBaseUrl", "AzureSpeechService"),
            (RoutedServiceNames.SpeechSynthesis, "LocalServiceHosts:SpeechSynthesisBaseUrl", "AzureSpeechService"),
            (RoutedServiceNames.DocumentIntelligence, "LocalServiceHosts:DocumentIntelligenceBaseUrl", "AzureDocumentIntelligence")
        })
        {
            var modes = await service.GetServiceModesAsync(serviceName);
            modes.Should().HaveCount(1, "legacy/default rows should normalize only the persisted mode without synthesizing counterparts");
            modes.Should().Contain(m =>
                m.ModeId == "local" &&
                m.ProviderSection == expectedLocalProviderSection &&
                m.Enabled &&
                m.IsDefault);
        }
    }

    [TestMethod]
    public async Task BootstrapAsync_NormalizesSingleLocalMode_WithoutAddingCloudCounterpart()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        var preSeeded = new System.Text.Json.Nodes.JsonObject();
        ServiceModesPayload.WriteModesFor(
            preSeeded,
            RoutedServiceNames.Embeddings,
            new[]
            {
                new ServiceMode(
                    ModeId: "local",
                    ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                    ModelId: null,
                    RequestPresetJson: null,
                    Enabled: true,
                    IsDefault: true)
            },
            defaultModeId: "local");

        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = preSeeded.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, configuration);
        await service.BootstrapAsync(configuration);

        var modes = await service.GetServiceModesAsync(RoutedServiceNames.Embeddings);
        modes.Should().HaveCount(1);
        modes.Should().Contain(m =>
            m.ModeId == "local" &&
            m.ProviderSection == "LocalServiceHosts:EmbeddingsBaseUrl" &&
            m.IsDefault);
    }

    [TestMethod]
    public async Task BootstrapAsync_DoesNotMigrate_WhenLegacyLeafIsOnlyInConfiguration()
    {
        // Even if somebody plants legacy {Service}:ActiveProviderId keys into
        // IConfiguration, bootstrap does not synthesize modes from them.
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Embeddings:ActiveProviderId"] = ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
            ["ImageGeneration:ActiveProviderId"] = ServiceProviderIds.ImageGenerationAzureOpenAiImages,
            ["SpeechTranscription:ActiveProviderId"] = ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch,
            ["SpeechSynthesis:ActiveProviderId"] = ServiceProviderIds.SpeechSynthesisAzureSpeechSsml,
            ["DocumentIntelligence:ActiveProviderId"] = ServiceProviderIds.DocumentIntelligenceAzure
        });
        var service = CreateService(db, configuration);

        await service.BootstrapAsync(configuration);

        foreach (var serviceName in RoutedServiceNames.All)
        {
            var modes = await service.GetServiceModesAsync(serviceName);
            modes.Should().BeEmpty(
                "bootstrap must not synthesize modes from IConfiguration legacy keys");
        }
    }

    [TestMethod]
    public async Task BootstrapAsync_Skips_WhenLegacyLeafMapsToUnknownProvider()
    {
        // Legacy per-service ActiveProviderId leaves are ignored; malformed
        // values must not produce synthesized modes.
        await using var db = CreateDbContext();

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["ActiveProviderId"] = "Embeddings.Nonsense.Unknown"
        };
        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = RoutedServiceNames.Embeddings,
            SchemaVersion = 1,
            JsonValue = payload.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var configuration = BuildConfigurationWithoutActiveProviderIds();
        var service = CreateService(db, configuration);

        await service.BootstrapAsync(configuration);

        var modes = await service.GetServiceModesAsync(RoutedServiceNames.Embeddings);
        modes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BootstrapAsync_DoesNotReadActiveProviderId_WhenServiceModesExists()
    {
        // Phase H.3 (R-4.4): when the ServiceModes section already has at least one
        // mode per routed service, bootstrap MUST NOT read any legacy
        // {Service}:ActiveProviderId key from IConfiguration.
        await using var db = CreateDbContext();

        var preSeeded = new System.Text.Json.Nodes.JsonObject();
        foreach (var serviceName in RoutedServiceNames.All)
        {
            GuideAntsApi.Services.Routing.ServiceModesPayload.WriteModesFor(
                preSeeded,
                serviceName,
                new[]
                {
                    new GuideAntsApi.Services.Routing.ServiceMode(
                        ModeId: "default",
                        ProviderSection: "AzureOpenAiEmbedding",
                        ModelId: null,
                        RequestPresetJson: null,
                        Enabled: true,
                        IsDefault: true)
                },
                defaultModeId: "default");
        }
        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = GuideAntsApi.Services.Routing.ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = preSeeded.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var inner = BuildConfiguration(new Dictionary<string, string?>
        {
            ["SpeechTranscription:ActiveProviderId"] = "tripwire-speech-transcription",
            ["SpeechSynthesis:ActiveProviderId"] = "tripwire-speech-synthesis",
            ["ImageGeneration:ActiveProviderId"] = "tripwire-image-generation",
            ["Embeddings:ActiveProviderId"] = "tripwire-embeddings",
            ["DocumentIntelligence:ActiveProviderId"] = "tripwire-document-intelligence"
        });
        var tracked = new TrackingConfiguration(inner);
        var service = CreateService(db, tracked);

        await service.BootstrapAsync(tracked);

        tracked.AccessedKeys.Should().NotContain(
            key => key.EndsWith(":ActiveProviderId", StringComparison.Ordinal),
            "R-4.4: bootstrap MUST NOT read any {Service}:ActiveProviderId key when ServiceModes already has entries for every routed service.");
    }

    /// <summary>
    /// <see cref="IConfiguration"/> decorator that records every key indexer
    /// lookup. Used by the R-4.4 regression test to assert that production
    /// code does not read legacy <c>ActiveProviderId</c> keys.
    /// </summary>
    private sealed class TrackingConfiguration : IConfiguration
    {
        private readonly IConfiguration _inner;
        public List<string> AccessedKeys { get; } = new();

        public TrackingConfiguration(IConfiguration inner)
        {
            _inner = inner;
        }

        public string? this[string key]
        {
            get
            {
                AccessedKeys.Add(key);
                return _inner[key];
            }
            set => _inner[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => _inner.GetChildren();
        public IChangeToken GetReloadToken() => _inner.GetReloadToken();
        public IConfigurationSection GetSection(string key) => _inner.GetSection(key);
    }

    [TestMethod]
    public async Task BootstrapAsync_DoesNotOverrideExistingServiceModes()
    {
        await using var db = CreateDbContext();
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        // Seed the ServiceModes row directly so bootstrap observes an existing
        // "custom" Embeddings mode and leaves it untouched.
        var preSeeded = new System.Text.Json.Nodes.JsonObject();
        GuideAntsApi.Services.Routing.ServiceModesPayload.WriteModesFor(
            preSeeded,
            RoutedServiceNames.Embeddings,
            new[]
            {
                new GuideAntsApi.Services.Routing.ServiceMode(
                    ModeId: "custom",
                    ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                    ModelId: null,
                    RequestPresetJson: null,
                    Enabled: true,
                    IsDefault: true)
            },
            defaultModeId: "custom");
        db.ApplicationSettings.Add(new GuideAntsApi.DataModel.Models.ApplicationSetting
        {
            SectionName = GuideAntsApi.Services.Routing.ServiceModeResolver.SectionName,
            SchemaVersion = 1,
            JsonValue = preSeeded.ToJsonString(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, configuration);
        await service.BootstrapAsync(configuration);

        var modes = await service.GetServiceModesAsync(RoutedServiceNames.Embeddings);
        modes.Should().HaveCount(1, "bootstrap must keep explicitly configured service modes");
        modes[0].ModeId.Should().Be("custom");
        modes[0].ProviderSection.Should().Be("LocalServiceHosts:EmbeddingsBaseUrl");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"settings-schema-readiness-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static ApplicationSettingsService CreateService(ApplicationDbContext db, IConfiguration configuration)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.ContentRootPath).Returns(AppContext.BaseDirectory);

        var settingsSecrets = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        settingsSecrets.SetupGet(value => value.CurrentValue).Returns(CreateValidSecretsOptions());

        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();

        return new ApplicationSettingsService(
            db,
            new SettingsSectionRegistry(),
            environment.Object,
            configuration,
            settingsSecrets.Object,
            runtimeProfileResolver.Object);
    }

    private static SettingsSecretsOptions CreateValidSecretsOptions() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
        }
    };

    private static IConfiguration BuildConfigurationWithoutActiveProviderIds() =>
        BuildConfiguration(new Dictionary<string, string?>
        {
            ["Embeddings:ActiveProviderId"] = null,
            ["ImageGeneration:ActiveProviderId"] = null,
            ["SpeechTranscription:ActiveProviderId"] = null,
            ["SpeechSynthesis:ActiveProviderId"] = null,
            ["DocumentIntelligence:ActiveProviderId"] = null
        });

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = "tests",
            ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
            ["Ui:RootPath"] = "./ui",
            ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp",
            ["ServiceRouting:Containers:guideants-ai:BaseUrl"] = "http://localhost:8110/sandbox",

            // The five {Service}:ActiveProviderId entries are retained only for
            // regression tripwires that assert bootstrap ignores these keys.
            ["SpeechTranscription:ActiveProviderId"] = ServiceProviderIds.SpeechTranscriptionAzureSpeechBatch,
            ["SpeechSynthesis:ActiveProviderId"] = ServiceProviderIds.SpeechSynthesisAzureSpeechSsml,
            ["ImageGeneration:ActiveProviderId"] = ServiceProviderIds.ImageGenerationAzureOpenAiImages,
            ["Embeddings:ActiveProviderId"] = ServiceProviderIds.EmbeddingsAzureOpenAiEmbedding,
            ["DocumentIntelligence:ActiveProviderId"] = ServiceProviderIds.DocumentIntelligenceAzure,

            ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:MediaBaseUrl"] = "http://localhost:8110",
            ["LocalServiceHosts:DocumentIntelligenceBaseUrl"] = "http://localhost:5001",

            ["AzureSpeechService:Endpoint"] = "https://speech.example.com/",
            ["AzureSpeechService:ApiKey"] = "test-speech-key",
            ["AzureSpeechService:Region"] = "eastus2",

            ["AzureOpenAiEmbedding:Endpoint"] = "https://embedding-api.example.com/",
            ["AzureOpenAiEmbedding:ApiKey"] = "test-embedding-key",
            ["AzureOpenAiEmbedding:Deployment"] = "text-embedding-3-small",

            ["AzureOpenAiImages:Endpoint"] = "https://image-api.example.com/",
            ["AzureOpenAiImages:ApiKey"] = "test-api-key",
            ["AzureOpenAiImages:Deployment"] = "flux-1",
            ["AzureOpenAiImages:EditModelDeployment"] = "flux-1-edit",

            ["AzureDocumentIntelligence:Endpoint"] = "https://doc-intel.example.com/",
            ["AzureDocumentIntelligence:ApiKey"] = "test-doc-intel-key",
            ["GoogleGeminiApi:ApiKey"] = "test-gemini-key",
            ["OpenRouter:ApiKey"] = "test-openrouter-key",
            ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
            ["HuggingFace:Token"] = "hf_test_token",
            ["HuggingFace:RouterBaseUrl"] = "https://router.huggingface.co/v1",
            ["OpenAI:ApiKey"] = "test-openai-key"
        };

        foreach (var pair in values)
        {
            defaults[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .Build();
    }
}
