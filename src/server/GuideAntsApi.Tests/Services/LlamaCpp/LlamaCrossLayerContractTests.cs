using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaCrossLayerContractTests
{
    private static readonly string ContractsDir = LlamaCatalogContractTests.ResolveContractsDirPublic();
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];

    [TestMethod]
    public void CatalogToQuantFixtures_ChainThroughDtos()
    {
        var catalogJson = File.ReadAllText(Path.Combine(ContractsDir, "catalog-get-response.fixture.json"));
        var quantJson = File.ReadAllText(Path.Combine(ContractsDir, "quant-group-response.fixture.json"));

        var catalog = JsonSerializer.Deserialize<LlamaCatalogResponseDto>(catalogJson, SerializerOptions)!;
        var quants = JsonSerializer.Deserialize<LlamaCatalogQuantsResponseDto>(quantJson, SerializerOptions)!;

        catalog.Models[0].Id.Should().Be(quants.CatalogId);
        catalog.CatalogVersion.Should().NotBeNullOrWhiteSpace();
        quants.Quants.Should().NotBeEmpty();
    }

    [TestMethod]
    public void CuratedAddToImmutableInputFixture_ShareIdentityFields()
    {
        using var addDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir, "curated-add-request.fixture.json")));
        using var immutableDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir, "immutable-operation-input.fixture.json")));

        var install = addDoc.RootElement.GetProperty("install");
        var immutable = immutableDoc.RootElement;

        install.GetProperty("catalogId").GetString().Should().Be(immutable.GetProperty("definitionId").GetString());
        install.GetProperty("quantId").GetString().Should().Be("q6_k_xl");
        install.GetProperty("resolvedRevision").GetString().Should().Be(immutable.GetProperty("resolvedRevision").GetString());
    }

    [TestMethod]
    public async Task CatalogQuantCuratedFlow_ProducesDurableOperation()
    {
        await using var db = CreateDb();
        SeedRuntimeProfile(db);

        var catalogJson = File.ReadAllText(Path.Combine(ContractsDir, "catalog-get-response.fixture.json"));
        var catalog = JsonSerializer.Deserialize<LlamaCatalogResponseDto>(catalogJson, SerializerOptions)!;
        var definition = catalog.Models[0];

        var adminClient = CreateAdminClient();
        var resolver = new CuratedInstallResolver(
            adminClient,
            CreateTokenResolver().Object,
            CreateProfileResolver().Object);

        var request = new AddModelRequest(
            Provider: "llama-cpp",
            Catalog: new AddModelCatalogDto(
                ModelId: "",
                DisplayName: definition.Display.Name,
                Description: definition.Display.Description,
                DisplayOrder: null,
                IsActive: true),
            ProviderConfig: null,
            Install: new AddModelInstallDto(
                Source: LocalModelInstallSources.Curated,
                Curated: new AddModelInstallCuratedDto(
                    CatalogId: definition.Id,
                    CatalogVersion: catalog.CatalogVersion,
                    QuantId: "q6_k_xl",
                    ResolvedRevision: "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a")));

        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);
        var input = await resolver.ResolveAsync(request, command, CancellationToken.None);

        input.DefinitionId.Should().Be(definition.Id);
        input.QuantId.Should().Be("q6_k_xl");
        input.ComputeHash().Should().StartWith("sha256:");
    }

    [TestMethod]
    public async Task OperatorManagedInstall_PreservesProvenanceWithoutInvention()
    {
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"qwen-local","runtimeProfileId":"qwen3_6"}""",
            Created = now,
            Updated = now,
        });
        db.LocalModelInstallations.Add(new LocalModelInstallation
        {
            ModelId = "qwen-local",
            ManagementMode = "operatorManaged",
            CatalogId = null,
            CatalogVersion = null,
            Repository = "org/model",
            RequestedRevision = "main",
            ResolvedRevision = "abc123",
            QuantId = "q6_k_xl",
            QuantLabel = "Q6_K_XL",
            RouterModelId = "qwen-local",
            TargetDirectory = "qwen-local",
            ModelArtifactsJson = InstallationArtifactRecords.SerializeFromPaths("qwen-local", ["model-q6.gguf"]),
            ProjectorArtifactsJson = "[]",
            RouterPresetSnapshotJson = """{"ctx-size":"8192"}""",
            CreatedUtc = now,
            UpdatedUtc = now,
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var installationService = new LocalModelInstallationService(db, inventory.Object);
        var detail = await installationService.GetDetailAsync("qwen-local");

        detail.CatalogId.Should().BeNull();
        detail.Repository.Should().Be("org/model");
        detail.RouterPresetSnapshot.Should().ContainKey("ctx-size");
    }

    [TestMethod]
    public void LifecycleFixtures_ParseRepairAndChangeQuantContracts()
    {
        using var repairDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir, "repair-request.fixture.json")));
        using var changeQuantDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir, "change-quant-request.fixture.json")));

        repairDoc.RootElement.TryGetProperty("verifyDigest", out _).Should().BeTrue();
        changeQuantDoc.RootElement.GetProperty("quantId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static void SeedRuntimeProfile(ApplicationDbContext db)
    {
        db.RuntimeProfiles.Add(new RuntimeProfile
        {
            ProfileId = "qwen3_6",
            DisplayName = "Qwen 3.6",
            SamplingParametersJson = "{}",
            ThinkingControlJson = """{"defaultChoice":"medium","choiceActions":{"medium":[]}}""",
            ProvidersJson = """["llama-cpp"]""",
            RequestFieldsWhenToolsPresentJson = """{"parallel_tool_calls":true}""",
            Created = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static Mock<IHuggingFaceTokenResolver> CreateTokenResolver()
    {
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");
        return tokenResolver;
    }

    private static Mock<IRuntimeProfileResolver> CreateProfileResolver()
    {
        var resolver = new Mock<IRuntimeProfileResolver>();
        resolver
            .Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuntimeProfileData(
                "qwen3_6",
                true,
                null,
                new Dictionary<string, SamplingParameterDefinition>(),
                new ThinkingControl("medium", new Dictionary<string, IReadOnlyList<ThinkingAction>>()),
                new Dictionary<string, JsonElement>()));
        return resolver;
    }

    private static ILlamaRuntimeAdminClient CreateAdminClient()
    {
        var catalogJson = File.ReadAllText(Path.Combine(ContractsDir, "catalog-get-response.fixture.json"));
        var quantsJson = File.ReadAllText(Path.Combine(ContractsDir, "quant-group-response.fixture.json"));
        var catalog = JsonSerializer.Deserialize<LlamaCatalogResponseDto>(catalogJson, SerializerOptions)!;
        var quants = JsonSerializer.Deserialize<LlamaCatalogQuantsResponseDto>(quantsJson, SerializerOptions)!;

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(catalog);
        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                catalog.Models[0].Id,
                catalog.CatalogVersion,
                "hf_token",
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(quants);
        return adminClient.Object;
    }
}
