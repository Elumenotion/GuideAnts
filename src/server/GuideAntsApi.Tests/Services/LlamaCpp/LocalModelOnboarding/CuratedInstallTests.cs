using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GuideAntsApi.Tests.TestUtils;

[TestClass]
public sealed class CuratedInstallTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];
    private const string Revision = "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a";

    [TestMethod]
    public async Task ValidateAsync_CuratedForbiddenFields_Throws()
    {
        var validator = CreateValidator();
        var request = CreateCuratedRequest(install =>
            install with
            {
                RouterModelId = "forbidden-alias",
            });

        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);
        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.CuratedForbiddenField);
    }

    [TestMethod]
    public async Task ValidateAsync_CuratedSameTargetAlreadyInstalled_DoesNotThrow()
    {
        // A prior (e.g. text-only) install of the same curated definition exists and its catalog row
        // owns the router alias. Re-installing the current definition must reconcile, not throw.
        var runtimeConfigJson = LocalRuntimeConfigurationParser.SerializeCanonical(
            new LocalRuntimeConfiguration("Qwen3.6-35B-A3B-MTP-GGUF"));
        var validator = CreateValidator(
            models:
            [
                new SettingsModelDto(
                    "qwen3.6-35b-a3b-mtp-local", "Qwen 3.6 35B A3B MTP", "llama-cpp", null, null,
                    runtimeConfigJson, true, null, DateTime.UtcNow, null),
            ],
            inventory:
            [
                new LlamaRuntimeInventoryItemDto(
                    "Qwen3.6-35B-A3B-MTP-GGUF", "unloaded", "/models/model.gguf", null, true, false,
                    ["qwen3.6-35b-a3b-mtp-local"], 0),
            ]);

        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateAsync_CuratedModelIdReusedForDifferentTarget_ThrowsModelIdTaken()
    {
        // Same catalogModelId but a different runtime configuration (different alias) is a genuine
        // conflict, not a reconcile.
        var runtimeConfigJson = LocalRuntimeConfigurationParser.SerializeCanonical(
            new LocalRuntimeConfiguration("some-other-alias"));
        var validator = CreateValidator(
            models:
            [
                new SettingsModelDto(
                    "qwen3.6-35b-a3b-mtp-local", "Different", "llama-cpp", null, null,
                    runtimeConfigJson, true, null, DateTime.UtcNow, null),
            ]);

        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("MODEL_ID_TAKEN");
    }

    [TestMethod]
    public async Task ValidateAsync_CuratedAliasOwnedByDifferentCatalogRow_ThrowsRouterAliasTaken()
    {
        var validator = CreateValidator(
            inventory:
            [
                new LlamaRuntimeInventoryItemDto(
                    "Qwen3.6-35B-A3B-MTP-GGUF", "unloaded", "/models/model.gguf", null, true, false,
                    ["some-other-model"], 0),
            ]);

        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("ROUTER_ALIAS_TAKEN");
    }

    [TestMethod]
    public async Task Resolver_CatalogVersionMismatch_Throws()
    {
        var adminClient = CreateAdminClient(catalogVersion: "2026-01-01");
        var resolver = CreateResolver(adminClient);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.CatalogVersionUnavailable);
    }

    [TestMethod]
    public async Task Resolver_CommitChanged_Throws()
    {
        var adminClient = CreateAdminClient(headRevision: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var resolver = CreateResolver(adminClient);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.CommitChanged);
    }

    [TestMethod]
    public async Task Resolver_QuantMissing_Throws()
    {
        var adminClient = CreateAdminClient(quantId: "missing-quant");
        var resolver = CreateResolver(adminClient);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.QuantMissing);
    }

    [TestMethod]
    public async Task Resolver_SingleGguf_BuildsImmutableInput()
    {
        var adminClient = CreateAdminClient(singleGguf: true);
        var resolver = CreateResolver(adminClient);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var input = await resolver.ResolveAsync(request, command, CancellationToken.None);

        input.ModelFiles.Should().ContainSingle().Which.Should().EndWith(".gguf");
        input.RouterPreset.Should().ContainKey("ctx-size");
        input.ComputeHash().Should().StartWith("sha256:");
    }

    [TestMethod]
    public async Task Resolver_ShardedQuant_ValidatesCompleteGroup()
    {
        var adminClient = CreateAdminClient(singleGguf: false);
        var resolver = CreateResolver(adminClient);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var input = await resolver.ResolveAsync(request, command, CancellationToken.None);

        input.ModelFiles.Should().HaveCount(2);
        input.ModelFiles[0].Should().Contain("00001-of-00002");
    }

    [TestMethod]
    public async Task OperationService_DuplicateInputHash_ReusesActiveOperation()
    {
        await using var db = CreateDbContext();
        var input = CreateImmutableInput(singleGguf: true);
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "downloading",
            CurrentStep = "downloading",
            CompletedSideEffectsJson = "{}",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var service = CreateOperationService(db);
        var found = await service.FindActiveByInputHashAsync(input.ComputeHash(), CancellationToken.None);

        found.Should().NotBeNull();
        found!.OperationId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [TestMethod]
    public async Task OperationService_FinalizationRetry_CommitsModelAndProvenance()
    {
        await using var db = CreateDbContext();        var input = CreateImmutableInput(singleGguf: true);
        var operationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "catalogFinalization",
            CurrentStep = "catalogFinalization",
            CompletedSideEffectsJson = """{"downloadStarted":true,"artifactsActivated":true,"aliasRegistered":true}""",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var service = CreateOperationService(db, adminClient.Object);
        var status = await service.ReconcileAndGetStatusAsync(operationId, CancellationToken.None);

        status.Status.Should().Be("completed");
        status.CatalogModel.Should().NotBeNull();
        status.CatalogModel!.RuntimeConfigJson.Should().Contain("routerModelId");
        status.CatalogModel.RuntimeConfigJson.Should().NotContain("loadParams");

        var installation = await db.LocalModelInstallations.SingleAsync(x => x.ModelId == input.CatalogModelId);
        installation.ManagementMode.Should().Be("curated");
        installation.QuantId.Should().Be("q6_k_xl");
    }

    [TestMethod]
    public async Task OperationService_ReconcileExistingInstall_UpdatesProjectorProvenance()
    {
        await using var db = CreateDbContext();        var input = CreateImmutableInput(singleGguf: true);
        var now = DateTime.UtcNow;

        // Prior text-only install: catalog row + provenance with NO projector recorded.
        db.Models.Add(new Model
        {
            ModelId = input.CatalogModelId,
            DisplayName = input.CatalogDisplayName,
            Provider = "llama-cpp",
            RuntimeConfigJson = LocalRuntimeConfigurationParser.SerializeCanonical(
                new LocalRuntimeConfiguration(input.RouterModelId)),
            CombineSystemAndDeveloperMessages = true,
            SamplingParametersJson = "{}",
            ThinkingControlJson = """{"defaultChoice":"medium","choiceActions":{"medium":[]}}""",
            RequestFieldsWhenToolsPresentJson = "{}",
            IsActive = true,
            Created = now,
            Updated = now,
        });
        db.LocalModelInstallations.Add(new LocalModelInstallation
        {
            ModelId = input.CatalogModelId,
            ManagementMode = "curated",
            CatalogId = input.DefinitionId,
            CatalogVersion = input.DefinitionVersion,
            Repository = input.Repository,
            ResolvedRevision = input.ResolvedRevision,
            QuantId = input.QuantId,
            QuantLabel = input.QuantLabel,
            RouterModelId = input.RouterModelId,
            TargetDirectory = input.TargetDirectory,
            ModelArtifactsJson = "[]",
            ProjectorArtifactsJson = "[]",
            RouterPresetSnapshotJson = "{}",
            CreatedUtc = now,
            UpdatedUtc = now,
            RowVersion = DefaultRowVersion,
        });

        var operationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "catalogFinalization",
            CurrentStep = "catalogFinalization",
            CompletedSideEffectsJson = """{"downloadStarted":true,"artifactsActivated":true,"aliasRegistered":true}""",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var service = CreateOperationService(db, adminClient.Object);
        var status = await service.ReconcileAndGetStatusAsync(operationId, CancellationToken.None);

        status.Status.Should().Be("completed");
        (await db.Models.CountAsync()).Should().Be(1);
        var installation = await db.LocalModelInstallations.SingleAsync(x => x.ModelId == input.CatalogModelId);
        installation.ProjectorArtifactsJson.Should().Contain("mmproj-F16.gguf");
        installation.ModelArtifactsJson.Should().Contain("Qwen3.6-35B-A3B-Q6_K_XL.gguf");
    }

    [TestMethod]
    public async Task OperationService_ApiRestart_ResumesFromCatalogFinalization()
    {
        await using var db = CreateDbContext();        var input = CreateImmutableInput(singleGguf: false);
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "catalogFinalization",
            CurrentStep = "catalogFinalization",
            CompletedSideEffectsJson = """{"downloadStarted":true,"artifactsActivated":true,"aliasRegistered":true}""",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var service = CreateOperationService(db);
        await service.ReconcileInFlightOperationsAsync(CancellationToken.None);

        var operation = await db.LocalModelOperations.SingleAsync(x => x.OperationId == operationId);
        operation.Status.Should().Be("completed");
        (await db.Models.CountAsync()).Should().Be(1);
        (await db.LocalModelInstallations.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task OperationService_Downloading_ReportsAdminProgress()
    {
        await using var db = CreateDbContext();        var input = CreateImmutableInput(singleGguf: true);
        var operationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "downloading",
            CurrentStep = "downloading",
            CompletedSideEffectsJson = """{"downloadStarted":true}""",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient
            .Setup(x => x.StartExactDownloadAsync(It.IsAny<ExactStartModelDownloadRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: operationId.ToString("D"),
                Status: "downloading",
                RouterModelId: input.RouterModelId,
                Progress: 0.42,
                ErrorMessage: null,
                LogLine: "Downloading model.gguf",
                ImmutableInputHash: input.ComputeHash()));
        adminClient
            .Setup(x => x.GetDownloadStatusAsync(operationId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: operationId.ToString("D"),
                Status: "downloading",
                RouterModelId: input.RouterModelId,
                Progress: 0.42,
                ErrorMessage: null,
                LogLine: "Downloading model.gguf",
                ImmutableInputHash: input.ComputeHash()));

        var service = CreateOperationService(db, adminClient.Object);
        var status = await service.ReconcileAndGetStatusAsync(operationId, CancellationToken.None);

        status.Status.Should().Be("downloading");
        status.Progress.Should().Be(0.42);
        status.LogLine.Should().Be("Downloading model.gguf");
    }

    [TestMethod]
    public async Task OperationService_LlamaAdminCompleted_MovesToCatalogFinalization()
    {
        await using var db = CreateDbContext();        var input = CreateImmutableInput(singleGguf: true);
        var operationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "registeringAlias",
            CurrentStep = "registeringAlias",
            CompletedSideEffectsJson = """{"downloadStarted":true}""",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient
            .Setup(x => x.GetDownloadStatusAsync(operationId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: operationId.ToString("D"),
                Status: "completed",
                RouterModelId: input.RouterModelId,
                Progress: 1,
                ErrorMessage: null,
                LogLine: "done",
                ImmutableInputHash: input.ComputeHash()));

        var service = CreateOperationService(db, adminClient.Object);
        var status = await service.ReconcileAndGetStatusAsync(operationId, CancellationToken.None);

        status.Status.Should().Be("completed");
        adminClient.Verify(x => x.GetDownloadStatusAsync(operationId.ToString("D"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task OperationService_Downloading_DoesNotRestartLlamaAdminWorker()
    {
        await using var db = CreateDbContext();        var input = CreateImmutableInput(singleGguf: true);
        var operationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = input.CatalogModelId,
            RouterModelId = input.RouterModelId,
            ImmutableInputJson = input.ToJson(),
            Status = "downloading",
            CurrentStep = "downloading",
            CompletedSideEffectsJson = """{"downloadStarted":true}""",
            RowVersion = DefaultRowVersion,
        });
        await db.SaveChangesAsync();

        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient
            .Setup(x => x.GetDownloadStatusAsync(operationId.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: operationId.ToString("D"),
                Status: "downloading",
                RouterModelId: input.RouterModelId,
                Progress: 0.42,
                ErrorMessage: null,
                LogLine: "Downloading model.gguf",
                ImmutableInputHash: input.ComputeHash()));

        var service = CreateOperationService(db, adminClient.Object);
        var status = await service.ReconcileAndGetStatusAsync(operationId, CancellationToken.None);

        status.Status.Should().Be("downloading");
        status.Progress.Should().Be(0.42);
        adminClient.Verify(
            x => x.StartExactDownloadAsync(It.IsAny<ExactStartModelDownloadRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        adminClient.Verify(
            x => x.GetDownloadStatusAsync(operationId.ToString("D"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static LocalModelOnboardingValidator CreateValidator(
        ICuratedInstallResolver? resolver = null,
        IReadOnlyList<SettingsModelDto>? models = null,
        IReadOnlyList<LlamaRuntimeInventoryItemDto>? inventory = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var settingsService = new Mock<IApplicationSettingsService>();
        settingsService.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(models ?? Array.Empty<SettingsModelDto>());

        var chatTargetValidator = new Mock<IChatTargetValidator>();
        chatTargetValidator.Setup(x => x.Validate(It.IsAny<ChatTarget>()));

        var inventoryService = new Mock<ILlamaRuntimeInventoryService>();
        inventoryService.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(inventory ?? Array.Empty<LlamaRuntimeInventoryItemDto>());

        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");

        return new LocalModelOnboardingValidator(
            configuration,
            settingsService.Object,
            chatTargetValidator.Object,
            inventoryService.Object,
            tokenResolver.Object,
            resolver ?? CreateResolver(CreateAdminClient()),
            new Mock<ICustomInstallResolver>(MockBehavior.Strict).Object);
    }

    private static CuratedInstallResolver CreateResolver(ILlamaRuntimeAdminClient adminClient)
    {
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");

        return new CuratedInstallResolver(
            adminClient,
            tokenResolver.Object);
    }

    private static LocalModelOperationService CreateOperationService(
        ApplicationDbContext db,
        ILlamaRuntimeAdminClient? adminClient = null)
    {
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");

        return new LocalModelOperationService(
            db,
            adminClient ?? new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            tokenResolver.Object,
            NullLogger<LocalModelOperationService>.Instance);
    }

    private static ILlamaRuntimeAdminClient CreateAdminClient(
        string catalogVersion = "2026-07-10",
        string headRevision = Revision,
        string quantId = "q6_k_xl",
        bool singleGguf = false)
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient
            .Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalog(catalogVersion));

        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                "qwen3.6-35b-a3b-mtp",
                "2026-07-10",
                "hf_token",
                It.IsAny<CancellationToken>(),
                null))
            .ReturnsAsync(CreateQuants(headRevision, quantId, singleGguf));

        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                "qwen3.6-35b-a3b-mtp",
                "2026-07-10",
                "hf_token",
                It.IsAny<CancellationToken>(),
                Revision))
            .ReturnsAsync(CreateQuants(Revision, quantId, singleGguf));

        return adminClient.Object;
    }

    private static LlamaCatalogResponseDto CreateCatalog(string version) =>
        new(
            SchemaVersion: 1,
            Task: "llama",
            CatalogVersion: version,
            Models:
            [
                new LlamaCatalogDefinitionDto(
                    Id: "qwen3.6-35b-a3b-mtp",
                    Display: new LlamaCatalogDisplayDto(
                        Name: "Qwen 3.6 35B A3B MTP",
                        Description: "Curated",
                        Labels: ["Text", "Vision", "MTP"],
                        License: "Apache-2.0",
                        DocumentationUrl: "https://example.com"),
                    Source: new LlamaCatalogSourceDto("unsloth/Qwen3.6-35B-A3B-MTP-GGUF", "main"),
                    Defaults: LlamaCatalogTestHelpers.CreateDefaults(
                        "qwen3.6-35b-a3b-mtp-local",
                        "Qwen3.6-35B-A3B-MTP-GGUF",
                        "Qwen3.6-35B-A3B-MTP-GGUF",
                        new LlamaCatalogMmprojDto("mmproj-F16.gguf"),
                        new Dictionary<string, string>
                        {
                            ["ctx-size"] = "131072",
                            ["image-min-tokens"] = "1024",
                            ["spec-type"] = "draft-mtp",
                            ["spec-draft-n-max"] = "2",
                        }),
                    QuantMetadata: new LlamaCatalogQuantMetadataDto(),
                    HardwareNotes: new LlamaCatalogHardwareNotesDto("notes", "large")),
            ]);

    private static LlamaCatalogQuantsResponseDto CreateQuants(
        string revision,
        string quantId,
        bool singleGguf)
    {
        var quants = singleGguf
            ? new List<LlamaQuantGroupDto>
            {
                new(
                    Id: quantId,
                    Label: "Q6_K_XL",
                    TotalBytes: 100,
                    Files: [new LlamaQuantArtifactDto("Qwen3.6-35B-A3B-Q6_K_XL.gguf", 100)]),
            }
            : new List<LlamaQuantGroupDto>
            {
                new(
                    Id: quantId,
                    Label: "Q6_K_XL",
                    TotalBytes: 200,
                    Files:
                    [
                        new LlamaQuantArtifactDto("Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf", 100, 1, 2),
                        new LlamaQuantArtifactDto("Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf", 100, 2, 2),
                    ]),
            };

        return new LlamaCatalogQuantsResponseDto(
            CatalogId: "qwen3.6-35b-a3b-mtp",
            Repository: "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            RequestedRevision: "main",
            ResolvedRevision: revision,
            Quants: quants,
            Projector: new LlamaProjectorArtifactDto("mmproj-F16.gguf", 900_000_000));
    }

    private static CuratedImmutableOperationInput CreateImmutableInput(bool singleGguf)
    {
        var chatBehavior = LlamaCatalogTestHelpers.RowOwnedChatBehaviorFields();
        return new CuratedImmutableOperationInput(
            DefinitionId: "qwen3.6-35b-a3b-mtp",
            DefinitionVersion: "2026-07-10",
            CatalogModelId: "qwen3.6-35b-a3b-mtp-local",
            CatalogDisplayName: "Qwen 3.6 35B A3B MTP",
            CatalogDescription: "Curated",
            CatalogDisplayOrder: null,
            CatalogIsActive: true,
            Repository: "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            RequestedRevision: "main",
            ResolvedRevision: Revision,
            QuantId: "q6_k_xl",
            QuantLabel: "Q6_K_XL",
            ModelFiles: singleGguf
                ? ["Qwen3.6-35B-A3B-Q6_K_XL.gguf"]
                :
                [
                    "Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf",
                    "Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf",
                ],
            MmprojFiles: ["mmproj-F16.gguf"],
            RouterModelId: "Qwen3.6-35B-A3B-MTP-GGUF",
            TargetDirectory: "Qwen3.6-35B-A3B-MTP-GGUF",
            RouterPreset: new Dictionary<string, string>
            {
                ["ctx-size"] = "131072",
                ["image-min-tokens"] = "1024",
                ["spec-type"] = "draft-mtp",
                ["spec-draft-n-max"] = "2",
            },
            SamplingParametersJson: chatBehavior.Sampling,
            ReasoningChoicesJson: chatBehavior.Reasoning,
            ThinkingControlJson: chatBehavior.Thinking,
            RequestFieldsWhenToolsPresentJson: chatBehavior.RequestFields,
            CombineSystemAndDeveloperMessages: chatBehavior.Combine,
            ThoughtBlockPattern: chatBehavior.Thought);
    }

    private static AddModelRequest CreateCuratedRequest(Func<AddModelInstallDto, AddModelInstallDto>? mutate = null)
    {
        var install = new AddModelInstallDto(
            Source: LocalModelInstallSources.Curated,
            Curated: new AddModelInstallCuratedDto(
                CatalogId: "qwen3.6-35b-a3b-mtp",
                CatalogVersion: "2026-07-10",
                QuantId: "q6_k_xl",
                ResolvedRevision: Revision));

        if (mutate is not null)
        {
            install = mutate(install);
        }

        return new AddModelRequest(
            Provider: "llama-cpp",
            Catalog: new AddModelCatalogDto(
                ModelId: "",
                DisplayName: "Qwen 3.6 35B A3B MTP",
                Description: null,
                DisplayOrder: null,
                IsActive: true),
            ProviderConfig: null,
            Install: install);
    }


    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"curated-install-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}
