using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GuideAntsApi.Tests.TestUtils;
using Moq;

/// <summary>
/// Guards the two invariants that together stop a router alias from being
/// permanently blocked by a stranded operation:
/// <list type="number">
/// <item>Operation status polling dispatches on <c>OperationKind</c>, so a lifecycle
/// operation is never driven by the curated-install state machine.</item>
/// <item>Any status that blocks new operations on an alias is selected by its owning
/// reconciler sweep, so it can always be advanced or failed.</item>
/// </list>
/// </summary>
[TestClass]
public sealed class LocalModelOperationDispatchTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];

    [TestMethod]
    public async Task StatusPoll_LifecycleOperation_IsNotDrivenByCuratedStateMachine()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db);
        var operationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SeedRepairOperation(db, operationId, status: "provenanceFinalization", downloadStarted: true);

        // Strict with no setups: any call into the curated service fails the test.
        var curatedService = new Mock<ILocalModelOperationService>(MockBehavior.Strict);
        var orchestrator = CreateOrchestrator(db, curatedService);

        var status = await orchestrator.GetCuratedOperationStatusAsync(operationId, CancellationToken.None);

        status.Should().NotBeNull();
        status!.Status.Should().Be("completed");
        curatedService.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task StatusPoll_LegacyRoute_LifecycleOperation_IsNotDrivenByCuratedStateMachine()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db);
        var operationId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        SeedRepairOperation(db, operationId, status: "provenanceFinalization", downloadStarted: true);

        var curatedService = new Mock<ILocalModelOperationService>(MockBehavior.Strict);
        var orchestrator = CreateOrchestrator(db, curatedService);

        var status = await orchestrator.GetOperationStatusAsync(operationId.ToString("D"), CancellationToken.None);

        status.Should().NotBeNull();
        status!.Status.Should().Be("completed");
        curatedService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A status outside the lifecycle vocabulary must not survive a sweep. Before the
    /// fix, a lifecycle row stamped with a curated-only status blocked its alias while
    /// no sweep selected it, so the alias could never be used again.
    /// </summary>
    [TestMethod]
    public async Task Sweep_LifecycleOperationOnUnownedStatus_ReachesTerminalStatus()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db);
        var operationId = Guid.Parse("cccccccc-dddd-eeee-ffff-000000000000");
        SeedRepairOperation(db, operationId, status: "catalogFinalization", downloadStarted: true);

        var adminClient = new Mock<ILlamaRuntimeAdminClient>();
        adminClient
            .Setup(x => x.GetDownloadStatusAsync(operationId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelDownloadOperationDto?)null);

        var service = CreateLifecycleOperationService(db, adminClient.Object);
        await service.ReconcileInFlightLifecycleOperationsAsync(CancellationToken.None);

        var operation = await db.LocalModelOperations.SingleAsync(o => o.OperationId == operationId);
        LocalModelOperationStatuses.IsTerminal(operation.Status).Should().BeTrue();
    }

    /// <summary>
    /// The user-visible symptom: every change quant attempt answered 409
    /// OPERATION_IN_FLIGHT forever because a stranded row blocked the alias.
    /// </summary>
    [TestMethod]
    public async Task StartChangeQuant_AfterSweepOfStrandedOperation_IsNotBlocked()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db);
        var strandedId = Guid.Parse("dddddddd-eeee-ffff-0000-111111111111");
        SeedRepairOperation(db, strandedId, status: "catalogFinalization", downloadStarted: true);

        var adminClient = CreateAdminClientForChangeQuant();
        adminClient
            .Setup(x => x.GetDownloadStatusAsync(strandedId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelDownloadOperationDto?)null);

        var lifecycleOperations = CreateLifecycleOperationService(db, adminClient.Object);
        await lifecycleOperations.ReconcileInFlightLifecycleOperationsAsync(CancellationToken.None);

        var lifecycle = CreateLifecycleService(db, adminClient);
        var response = await lifecycle.StartChangeQuantAsync(
            "qwen-local",
            new ChangeQuantRequestDto("q4_k_m", "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a"),
            CancellationToken.None);

        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.Status.Should().Be("queued");
    }

    [TestMethod]
    public async Task StartChangeQuant_WhileOperationGenuinelyInFlight_IsRejected()
    {
        await using var db = CreateDbContext();
        SeedInstallation(db);
        SeedRepairOperation(
            db,
            Guid.Parse("eeeeeeee-ffff-0000-1111-222222222222"),
            status: "downloading",
            downloadStarted: true);

        var lifecycle = CreateLifecycleService(db, CreateAdminClientForChangeQuant());

        var act = () => lifecycle.StartChangeQuantAsync(
            "qwen-local",
            new ChangeQuantRequestDto("q4_k_m", "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<LocalModelLifecycleException>())
            .Which.Code.Should().Be(LocalModelLifecycleErrorCodes.OperationInFlight);
    }

    private static LocalModelOnboardingOrchestrator CreateOrchestrator(
        ApplicationDbContext db,
        Mock<ILocalModelOperationService> curatedService)
    {
        var services = new ServiceCollection();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new LocalModelOnboardingOrchestrator(
            new Mock<GuideAntsApi.Settings.IApplicationSettingsService>(MockBehavior.Strict).Object,
            new Mock<IHuggingFaceModelDownloadService>(MockBehavior.Strict).Object,
            new Mock<ICuratedInstallResolver>(MockBehavior.Strict).Object,
            curatedService.Object,
            new Mock<ICustomInstallResolver>(MockBehavior.Strict).Object,
            CreateLifecycleOperationService(db),
            new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            db,
            scopeFactory,
            NullLogger<LocalModelOnboardingOrchestrator>.Instance);
    }

    private static LocalModelLifecycleOperationService CreateLifecycleOperationService(
        ApplicationDbContext db,
        ILlamaRuntimeAdminClient? adminClient = null) =>
        new(
            db,
            adminClient ?? new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            new Mock<IHuggingFaceTokenResolver>().Object,
            new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict).Object,
            new Mock<ILlamaRuntimeCoordinator>().Object,
            NullLogger<LocalModelLifecycleOperationService>.Instance);

    private static LocalModelLifecycleService CreateLifecycleService(
        ApplicationDbContext db,
        Mock<ILlamaRuntimeAdminClient> adminClient)
    {
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlamaRuntimeInventoryItemDto>());

        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateLifecycleOperationService(db, adminClient.Object));

        return new LocalModelLifecycleService(
            db,
            new LocalModelInstallationService(db, inventory.Object),
            CreateLifecycleOperationService(db, adminClient.Object),
            adminClient.Object,
            tokenResolver.Object,
            inventory.Object,
            new Mock<ILlamaRuntimeCoordinator>().Object,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LocalModelLifecycleService>.Instance);
    }

    private static void SeedRepairOperation(
        ApplicationDbContext db,
        Guid operationId,
        string status,
        bool downloadStarted)
    {
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
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
            Status = status,
            CurrentStep = status,
            CompletedSideEffectsJson = downloadStarted
                ? """{"downloadStarted":true,"artifactsActivated":true,"aliasRegistered":true}"""
                : "{}",
            RowVersion = DefaultRowVersion,
        });
        db.SaveChanges();
    }

    private static Mock<ILlamaRuntimeAdminClient> CreateAdminClientForChangeQuant()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>();
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

    private static void SeedInstallation(ApplicationDbContext db)
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
            ManagementMode = "curated",
            CatalogId = "qwen3.6-35b-a3b",
            CatalogVersion = "2026-07-10",
            Repository = "org/model",
            RequestedRevision = "main",
            ResolvedRevision = "abc123",
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
