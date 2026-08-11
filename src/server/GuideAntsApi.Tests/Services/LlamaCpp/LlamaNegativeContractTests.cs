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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GuideAntsApi.Tests.TestUtils;
using Moq;

[TestClass]
public sealed class LlamaNegativeContractTests
{
    private const string Revision = "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a";

    [TestMethod]
    public async Task CuratedInstall_QuantMissing_ThrowsQuantMissing()
    {
        var resolver = CreateResolver(quantId: "missing-quant");
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.QuantMissing);
    }

    [TestMethod]
    public async Task CuratedInstall_CommitChanged_ThrowsCommitChanged()
    {
        var resolver = CreateResolver(headRevision: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.CommitChanged);
    }

    [TestMethod]
    public async Task CuratedInstall_IncompleteShards_ThrowsQuantIncomplete()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalog());
        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(new LlamaCatalogQuantsResponseDto(
                "qwen3.6-35b-a3b-mtp",
                "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
                "main",
                Revision,
                [
                    new LlamaQuantGroupDto(
                        "q6_k_xl",
                        "Q6_K_XL",
                        100,
                        [new LlamaQuantArtifactDto("shard-00001-of-00003.gguf", 50, 1, 3)]),
                ],
                null,
                []));

        var resolver = CreateResolver(adminClient.Object);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.QuantIncomplete);
    }

    [TestMethod]
    public async Task CuratedInstall_ProjectorMissing_ThrowsProjectorMissing()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalog(requireMmproj: true));
        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(new LlamaCatalogQuantsResponseDto(
                "qwen3.6-35b-a3b-mtp",
                "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
                "main",
                Revision,
                [new LlamaQuantGroupDto("q6_k_xl", "Q6_K_XL", 100, [new LlamaQuantArtifactDto("model.gguf", 100)])],
                null,
                []));

        var resolver = CreateResolver(adminClient.Object);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.ProjectorMissing);
    }

    [TestMethod]
    public async Task Validator_IdentityConflict_ThrowsModelIdTaken()
    {
        var settingsService = new Mock<IApplicationSettingsService>();
        settingsService
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SettingsModelDto(
                    "taken-id",
                    "Taken",
                    "llama-cpp",
                    null,
                    null,
                    null,
                    true,
                    null,
                    DateTime.UtcNow,
                    DateTime.UtcNow),
            ]);

        var validator = CreateValidator(settingsService.Object);
        var request = CreateCuratedRequest(mutate: install => install with
        {
            Curated = install.Curated! with { },
        });
        request = request with
        {
            Catalog = request.Catalog! with { ModelId = "taken-id" },
        };

        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);
        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("MODEL_ID_TAKEN");
    }

    [TestMethod]
    public void RouterPreset_RouterShellKey_ThrowsPresetInvalid()
    {
        var act = () => RouterPresetValidator.ValidateAndNormalize(new Dictionary<string, string>
        {
            ["models-preset"] = "/models-local/router-models.ini",
        });

        var ex = act.Should().Throw<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.PresetInvalid);
    }

    [TestMethod]
    public void RouterPreset_EnvDefaultParallel_AllowsPreset()
    {
        var preset = RouterPresetValidator.ValidateAndNormalize(new Dictionary<string, string>
        {
            ["parallel"] = "2",
            ["ctx-size"] = "131072",
            ["jinja"] = "true",
        });

        preset["parallel"].Should().Be("2");
        preset["ctx-size"].Should().Be("131072");
        preset["jinja"].Should().Be("true");
    }

    [TestMethod]
    public void RouterPreset_ModelScopedSpecType_AllowsPreset()
    {
        var preset = RouterPresetValidator.ValidateAndNormalize(new Dictionary<string, string>
        {
            ["spec-type"] = "draft-mtp",
            ["ctx-size"] = "131072",
        });

        preset["spec-type"].Should().Be("draft-mtp");
        preset["ctx-size"].Should().Be("131072");
    }

    [TestMethod]
    public void RouterPreset_ShellMetacharacter_ThrowsPresetInvalid()
    {
        var act = () => RouterPresetValidator.ValidateAndNormalize(new Dictionary<string, string>
        {
            ["ctx-size"] = "8192;rm -rf /",
        });

        act.Should().Throw<AddModelException>()
            .Which.Code.Should().Be(CuratedInstallErrorCodes.PresetInvalid);
    }

    [TestMethod]
    public async Task Lifecycle_ConcurrentAliasOperation_ThrowsAliasLockConflict()
    {
        await using var db = CreateDb();
        SeedInstallation(db);

        var coordinator = new Mock<ILlamaRuntimeCoordinator>();
        coordinator.Setup(x => x.IsAliasLocked("qwen-local")).Returns(true);

        var lifecycle = CreateLifecycleService(db, coordinator.Object);
        var act = async () => await lifecycle.StartRepairAsync(
            "qwen-local",
            new RepairInstallationRequestDto(Confirm: true),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<LocalModelLifecycleException>();
        ex.Which.Code.Should().Be(LocalModelLifecycleErrorCodes.AliasLockConflict);
    }

    [TestMethod]
    public async Task Lifecycle_Finalization_Completes_WithRowOwnedImmutableInput()
    {
        await using var db = CreateDb();
        var input = CreateImmutableInput();
        var operationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
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
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
        });
        await db.SaveChangesAsync();

        var operationService = new LocalModelOperationService(
            db,
            new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            Mock.Of<IHuggingFaceTokenResolver>(),
            NullLogger<LocalModelOperationService>.Instance);

        var status = await operationService.ReconcileAndGetStatusAsync(operationId, CancellationToken.None);
        status.Status.Should().Be("completed");

        var model = await db.Models.SingleAsync(m => m.ModelId == input.CatalogModelId);
        model.ThinkingControlJson.Should().Contain("medium");
    }

    [TestMethod]
    public async Task Resolver_GatedAccess_NoHfToken_ThrowsTokenMissing()
    {
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns((string?)null);

        var resolver = new CuratedInstallResolver(
            CreateAdminClient(Revision, "q6_k_xl").Object,
            tokenResolver.Object);
        var request = CreateCuratedRequest();
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be(CuratedInstallErrorCodes.HuggingFaceTokenMissing);
    }

    private static CuratedInstallResolver CreateResolver(
        ILlamaRuntimeAdminClient? adminClient = null,
        string headRevision = Revision,
        string quantId = "q6_k_xl")
    {
        adminClient ??= CreateAdminClient(headRevision, quantId).Object;
        return new CuratedInstallResolver(
            adminClient,
            CreateTokenResolver().Object);
    }

    private static Mock<ILlamaRuntimeAdminClient> CreateAdminClient(string headRevision, string quantId)
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        adminClient.Setup(x => x.GetCatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateCatalog());
        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                "qwen3.6-35b-a3b-mtp",
                "2026-07-10",
                "hf_token",
                It.IsAny<CancellationToken>(),
                null))
            .ReturnsAsync(CreateQuants(headRevision, quantId));
        adminClient
            .Setup(x => x.GetCatalogQuantsAsync(
                "qwen3.6-35b-a3b-mtp",
                "2026-07-10",
                "hf_token",
                It.IsAny<CancellationToken>(),
                Revision))
            .ReturnsAsync(CreateQuants(Revision, quantId));
        return adminClient;
    }

    private static LlamaCatalogResponseDto CreateCatalog(bool requireMmproj = false) =>
        new(
            1,
            "llama",
            "2026-07-10",
            [
                new LlamaCatalogDefinitionDto(
                    "qwen3.6-35b-a3b-mtp",
                    new LlamaCatalogDisplayDto("Qwen", "desc", [], "Apache-2.0", "https://example.com"),
                    new LlamaCatalogSourceDto("unsloth/Qwen3.6-35B-A3B-MTP-GGUF", "main"),
                    LlamaCatalogTestHelpers.CreateDefaults(
                        "qwen3.6-35b-a3b-mtp-local",
                        "Qwen3.6-35B-A3B-MTP-GGUF",
                        "Qwen3.6-35B-A3B-MTP-GGUF",
                        requireMmproj ? new LlamaCatalogMmprojDto("mmproj.gguf") : null,
                        routerPreset: new Dictionary<string, string> { ["ctx-size"] = "131072" }),
                    new LlamaCatalogQuantMetadataDto(),
                    new LlamaCatalogHardwareNotesDto("notes", "large")),
            ]);

    private static LlamaCatalogQuantsResponseDto CreateQuants(string revision, string quantId) =>
        new(
            "qwen3.6-35b-a3b-mtp",
            "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            "main",
            revision,
            [
                new LlamaQuantGroupDto(
                    quantId,
                    "Q6_K_XL",
                    100,
                    [new LlamaQuantArtifactDto("Qwen3.6-35B-A3B-Q6_K_XL.gguf", 100)]),
            ],
            null,
            []);

    private static LocalModelOnboardingValidator CreateValidator(
        IApplicationSettingsService? settingsService = null,
        IHuggingFaceTokenResolver? tokenResolver = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        settingsService ??= CreateDefaultSettingsService();
        tokenResolver ??= CreateTokenResolver().Object;

        return new LocalModelOnboardingValidator(
            configuration,
            settingsService,
            Mock.Of<IChatTargetValidator>(),
            Mock.Of<ILlamaRuntimeInventoryService>(),
            tokenResolver,
            CreateResolver(),
            new Mock<ICustomInstallResolver>(MockBehavior.Strict).Object);
    }

    private static IApplicationSettingsService CreateDefaultSettingsService()
    {
        var settingsService = new Mock<IApplicationSettingsService>();
        settingsService.Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return settingsService.Object;
    }

    private static Mock<IHuggingFaceTokenResolver> CreateTokenResolver()
    {
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");
        return tokenResolver;
    }

    private static CuratedImmutableOperationInput CreateImmutableInput()
    {
        var chatBehavior = LlamaCatalogTestHelpers.RowOwnedChatBehaviorFields();
        return new CuratedImmutableOperationInput(
            "qwen3.6-35b-a3b-mtp",
            "2026-07-10",
            "qwen3.6-35b-a3b-mtp-local",
            "Qwen",
            "desc",
            null,
            true,
            "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            "main",
            Revision,
            "q6_k_xl",
            "Q6_K_XL",
            ["model.gguf"],
            [],
            [],
            "Qwen3.6-35B-A3B-MTP-GGUF",
            "Qwen3.6-35B-A3B-MTP-GGUF",
            new Dictionary<string, string> { ["ctx-size"] = "131072" },
            chatBehavior.Sampling,
            chatBehavior.Reasoning,
            chatBehavior.Thinking,
            chatBehavior.RequestFields,
            chatBehavior.Combine,
            chatBehavior.Thought);
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
            Catalog: new AddModelCatalogDto("new-model", "Qwen", null, null, true),
            ProviderConfig: null,
            Install: install);
    }

    private static LocalModelLifecycleService CreateLifecycleService(
        ApplicationDbContext db,
        ILlamaRuntimeCoordinator coordinator)
    {
        var inventory = new Mock<ILlamaRuntimeInventoryService>();
        inventory.Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        return new LocalModelLifecycleService(
            db,
            new LocalModelInstallationService(db, inventory.Object),
            new LocalModelLifecycleOperationService(
                db,
                new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
                CreateTokenResolver().Object,
                new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict).Object,
                coordinator,
                NullLogger<LocalModelLifecycleOperationService>.Instance),
            new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            CreateTokenResolver().Object,
            inventory.Object,
            coordinator,
            CreateLifecycleScopeFactory(db, coordinator),
            NullLogger<LocalModelLifecycleService>.Instance);
    }

    private static IServiceScopeFactory CreateLifecycleScopeFactory(
        ApplicationDbContext db,
        ILlamaRuntimeCoordinator coordinator)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new LocalModelLifecycleOperationService(
            db,
            new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict).Object,
            CreateTokenResolver().Object,
            new Mock<ILlamaServerRuntimeClient>(MockBehavior.Strict).Object,
            coordinator,
            NullLogger<LocalModelLifecycleOperationService>.Instance));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static void SeedInstallation(ApplicationDbContext db)
    {
        var now = DateTime.UtcNow;
        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"qwen-local"}""",
            ThinkingControlJson = """{"defaultChoice":"medium","choiceActions":{"medium":[]}}""",
            SamplingParametersJson = "{}",
            RequestFieldsWhenToolsPresentJson = "{}",
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
            ResolvedRevision = Revision,
            QuantId = "q6_k_xl",
            QuantLabel = "Q6_K_XL",
            RouterModelId = "qwen-local",
            TargetDirectory = "qwen-local",
            ModelArtifactsJson = InstallationArtifactRecords.SerializeFromPaths("qwen-local", ["model-q6.gguf"]),
            ProjectorArtifactsJson = "[]",
            CompanionArtifactsJson = "[]",
            RouterPresetSnapshotJson = """{"ctx-size":"8192"}""",
            CreatedUtc = now,
            UpdatedUtc = now,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0],
        });
        db.SaveChanges();
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }
}
