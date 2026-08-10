using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GuideAntsApi.Tests.TestUtils;
using Moq;

[TestClass]
public sealed class LocalModelLifecycleTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];

    [TestMethod]
    public async Task AdoptionPreview_UnknownProvenance_BlockersPresent()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db, managementMode: "operatorManaged", includeRepository: false);
        var lifecycle = CreateLifecycleService(db);

        var preview = await lifecycle.PreviewAdoptAsync(
            "qwen-local",
            "qwen3.6-35b-a3b",
            "2026-07-10",
            CancellationToken.None);

        preview.CanAdopt.Should().BeFalse();
        preview.Blockers.Should().Contain(b => b.Contains("Repository", StringComparison.OrdinalIgnoreCase));
        preview.Differences.Should().Contain(d => d.Field == "repository" && !d.Verifiable);
    }

    [TestMethod]
    public async Task ChangeQuant_CreatesOperation_WithObsoletePaths()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db, managementMode: "curated");
        var adminClient = CreateAdminClientForChangeQuant();
        var lifecycle = CreateLifecycleService(db, adminClient: adminClient);

        var response = await lifecycle.StartChangeQuantAsync(
            "qwen-local",
            new ChangeQuantRequestDto("q4_k_m", "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a"),
            CancellationToken.None);

        response.OperationId.Should().NotBeNullOrWhiteSpace();
        var operation = await db.LocalModelOperations.SingleAsync();
        operation.OperationKind.Should().Be(LocalModelOperationKinds.ChangeQuant);

        var input = ChangeQuantImmutableInput.Deserialize(operation.ImmutableInputJson);
        input.ObsoleteRepositoryPaths.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task Repair_UsesRecordedCommitAndArtifacts()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db, managementMode: "curated");
        var lifecycle = CreateLifecycleService(db);

        var response = await lifecycle.StartRepairAsync(
            "qwen-local",
            new RepairInstallationRequestDto(Confirm: true),
            CancellationToken.None);

        var operation = await db.LocalModelOperations.SingleAsync();
        operation.OperationKind.Should().Be(LocalModelOperationKinds.Repair);
        var input = RepairImmutableInput.Deserialize(operation.ImmutableInputJson);
        input.ResolvedRevision.Should().Be("abc123");
        input.ModelFiles.Should().ContainSingle().Which.Should().Be("model-q6.gguf");
        response.Status.Should().Be("queued");
    }

    [TestMethod]
    public async Task LifecycleOperation_ProvenanceFinalization_RepairUpdatesTimestampOnly()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db, managementMode: "curated");
        var before = (await db.LocalModelInstallations.SingleAsync()).UpdatedUtc;

        var operation = new LocalModelOperation
        {
            OperationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            OperationKind = LocalModelOperationKinds.Repair,
            ModelId = "qwen-local",
            RouterModelId = "qwen-local",
            ImmutableInputJson = new RepairImmutableInput(
                "qwen-local",
                "org/model",
                "abc123",
                ["model-q6.gguf"],
                [],
                "qwen-local",
                "qwen-local",
                new Dictionary<string, string> { ["ctx-size"] = "8192" }).ToJson(),
            Status = "provenanceFinalization",
            CurrentStep = "provenanceFinalization",
            CompletedSideEffectsJson = """{"downloadStarted":true,"artifactsActivated":true,"aliasRegistered":true}""",
            RowVersion = DefaultRowVersion,
        };
        db.LocalModelOperations.Add(operation);
        await db.SaveChangesAsync();

        var service = CreateLifecycleOperationService(db);
        var status = await service.ReconcileLifecycleOperationAsync(operation.OperationId, CancellationToken.None);

        status.Status.Should().Be("completed");
        var installation = await db.LocalModelInstallations.SingleAsync();
        installation.UpdatedUtc.Should().BeAfter(before);
        installation.ResolvedRevision.Should().Be("abc123");
        installation.QuantId.Should().Be("q6_k_xl");
    }

    [TestMethod]
    public async Task DeleteRouterEntry_RuntimeFailure_PreservesCatalogRow()
    {
        var inventory = new[]
        {
            new LlamaRuntimeInventoryItemDto(
                RouterModelId: "qwen-local",
                RuntimeState: "loaded",
                ModelPath: "/models-local/llama/qwen/model.gguf",
                MmprojPath: null,
                HasModelFile: true,
                HasMmprojFile: false,
                CatalogModelIds: ["qwen-local"],
                NotebookReferenceCount: 0),
        };

        var inventoryService = new Mock<ILlamaRuntimeInventoryService>();
        inventoryService.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(inventory);

        var llamaClient = new Mock<ILlamaServerRuntimeClient>();
        llamaClient
            .Setup(x => x.UnloadModelAsync("qwen-local", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unload failed"));

        var settingsService = new Mock<GuideAntsApi.Settings.IApplicationSettingsService>(MockBehavior.Strict);

        var coordinator = new Mock<ILlamaRuntimeCoordinator>();
        coordinator.Setup(x => x.TryAcquireAliasLock("qwen-local")).Returns(new Mock<IAsyncDisposable>().Object);

        var result = await SettingsLlamaRouterDeleteHandler.DeleteLlamaRouterEntryAsync(
            "qwen-local",
            inventoryService.Object,
            llamaClient.Object,
            coordinator.Object,
            new Mock<ILlamaRuntimeAdminClient>().Object,
            settingsService.Object,
            CancellationToken.None);

        result.Should().NotBeNull();
        settingsService.Verify(x => x.DeleteModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void LlamaRouteGroup_RequiresAdminAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        var llamaGroup = SettingsGroupFactory.MapLlamaGroup(app);
        llamaGroup.MapGet("/installations/{modelId}", () => Results.Ok());

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => (e.RoutePattern.RawText ?? string.Empty).Contains("/installations/", StringComparison.Ordinal));

        endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy.Should().Be("RequireAdmin");
    }

    private static LocalModelInstallationService CreateInstallationService(ApplicationDbContext db)
    {
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());
        return new LocalModelInstallationService(db, inventory.Object);
    }

    private static LocalModelLifecycleService CreateLifecycleService(
        ApplicationDbContext db,
        Mock<ILlamaRuntimeAdminClient>? adminClient = null)
    {
        adminClient ??= CreateAdminClientForChangeQuant();
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");

        return new LocalModelLifecycleService(
            db,
            CreateInstallationService(db),
            CreateLifecycleOperationService(db, adminClient.Object),
            adminClient.Object,
            tokenResolver.Object,
            inventory.Object,
            new Mock<ILlamaRuntimeCoordinator>().Object,
            CreateLifecycleScopeFactory(db, adminClient.Object),
            NullLogger<LocalModelLifecycleService>.Instance);
    }

    private static IServiceScopeFactory CreateLifecycleScopeFactory(
        ApplicationDbContext db,
        ILlamaRuntimeAdminClient adminClient)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateLifecycleOperationService(db, adminClient));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static LocalModelLifecycleOperationService CreateLifecycleOperationService(
        ApplicationDbContext db,
        ILlamaRuntimeAdminClient? adminClient = null)
    {
        return new LocalModelLifecycleOperationService(
            db,
            adminClient ?? new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            new Mock<IHuggingFaceTokenResolver>().Object,
            new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict).Object,
            new Mock<ILlamaRuntimeCoordinator>().Object,
            NullLogger<LocalModelLifecycleOperationService>.Instance);
    }

    private static Mock<ILlamaRuntimeAdminClient> CreateAdminClientForChangeQuant()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCatalogResponseDto(
                1,
                "llama",
                "2026-07-10",
                [
                    new LlamaCatalogDefinitionDto(
                        "qwen3.6-35b-a3b",
                        new LlamaCatalogDisplayDto("Qwen", "desc", [], "Apache-2.0", "https://example.com"),
                        new LlamaCatalogSourceDto("org/model"),
                        new LlamaCatalogDefaultsDto(
                            "qwen-local",
                            "qwen-local",
                            "qwen-local",
                            null,
                            new Dictionary<string, string> { ["ctx-size"] = "8192" },
                            LlamaCatalogTestHelpers.CreateChatBehaviorDto()),
                        new LlamaCatalogQuantMetadataDto(),
                        new LlamaCatalogHardwareNotesDto("notes", "large")),
                ]));
        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                "qwen3.6-35b-a3b",
                "2026-07-10",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a"))
            .ReturnsAsync(new LlamaCatalogQuantsResponseDto(
                "qwen3.6-35b-a3b",
                "org/model",
                "main",
                "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a",
                [
                    new LlamaQuantGroupDto(
                        "q4_k_m",
                        "Q4_K_M",
                        1000,
                        [new LlamaQuantArtifactDto("model-q4.gguf", 1000)]),
                ],
                null));
        return adminClient;
    }

    private static void SeedInstallation(ApplicationDbContext db, string managementMode, bool includeRepository = true)
    {
        var now = DateTime.UtcNow;
        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"qwen-local","runtimeProfileId":"qwen3_6"}""",
            IsActive = true,
            Created = now,
            Updated = now,
        });
        db.LocalModelInstallations.Add(new LocalModelInstallation
        {
            ModelId = "qwen-local",
            ManagementMode = managementMode,
            CatalogId = "qwen3.6-35b-a3b",
            CatalogVersion = "2026-07-10",
            Repository = includeRepository ? "org/model" : null,
            RequestedRevision = includeRepository ? "main" : null,
            ResolvedRevision = includeRepository ? "abc123" : null,
            QuantId = "q6_k_xl",
            QuantLabel = "Q6_K_XL",
            RouterModelId = "qwen-local",
            TargetDirectory = "qwen-local",
            ModelArtifactsJson = InstallationArtifactRecords.SerializeFromPaths("qwen-local", ["model-q6.gguf"]),
            ProjectorArtifactsJson = "[]",
            RouterPresetSnapshotJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["ctx-size"] = "8192" }),
            CreatedUtc = now,
            UpdatedUtc = now,
            RowVersion = DefaultRowVersion,
        });
        db.SaveChanges();
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }
}
