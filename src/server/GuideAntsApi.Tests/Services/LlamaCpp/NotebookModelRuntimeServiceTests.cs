using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
[DoNotParallelize]
public class NotebookModelRuntimeServiceTests
{
    private Mock<ILlamaServerRuntimeClient> _mockLlamaClient = null!;
    private Mock<IChatModelResolver> _mockChatModelResolver = null!;
    private Mock<ILocalAiStartupWarmupService> _mockLocalAiWarmupService = null!;
    private Mock<ILocalAiWarmupService> _mockLocalAiWarmup = null!;
    private Mock<ILocalAiStackHostResolver> _mockStackHostResolver = null!;
    private Mock<ILlamaStackRuntimeClientProvider> _mockStackClientProvider = null!;
    private Mock<ILogger<NotebookModelRuntimeService>> _mockLogger = null!;
    private IMemoryCache _cache = null!;
    private ApplicationDbContext _context = null!;
    private NotebookModelRuntimeService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        ResetOperationState();

        _mockLlamaClient = new Mock<ILlamaServerRuntimeClient>();
        _mockChatModelResolver = new Mock<IChatModelResolver>();
        _mockLocalAiWarmupService = new Mock<ILocalAiStartupWarmupService>();
        // Default: pass the entity id through unchanged (Direct), which preserves the
        // legacy "preload exactly what the assistant references" contract these tests
        // were originally written against. Override per-test when asserting the new
        // override/default-chat-model behavior.
        _mockChatModelResolver
            .Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns<string?>(id => new ResolvedChatModel(
                id ?? string.Empty,
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    id ?? string.Empty,
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));
        _mockLocalAiWarmupService
            .Setup(s => s.UnloadAuxiliaryServicesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockLocalAiWarmupService
            .Setup(s => s.EnsureAuxiliaryServicesLoadedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockLocalAiWarmupService
            .Setup(s => s.IsWarmupInProgress)
            .Returns(false);
        _mockLocalAiWarmup = new Mock<ILocalAiWarmupService>();
        _mockLocalAiWarmup.SetupGet(s => s.IsApplyInProgress).Returns(false);
        _mockLocalAiWarmup
            .Setup(s => s.SyncDesiredAndApplyAsync(
                It.IsAny<WarmupDesiredBuildOptions?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockStackHostResolver = new Mock<ILocalAiStackHostResolver>();
        _mockStackHostResolver
            .Setup(r => r.GetAllConfiguredStackBases())
            .Returns(new List<string> { "http://localhost:8080" });
        _mockStackHostResolver
            .Setup(r => r.GetAllConfiguredStackBasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "http://localhost:8080" });
        _mockStackHostResolver
            .Setup(r => r.GetAllConfiguredInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalAiInstance>
            {
                new(LocalAiStackHostResolver.CanonicalInstanceKey("http://localhost:8080"), "http://localhost:8080")
            });
        _mockStackHostResolver
            .Setup(r => r.GetStackBaseForService(It.IsAny<string>()))
            .Returns<string?>(id => string.Equals(id, "llama", StringComparison.Ordinal)
                ? "http://localhost:8080"
                : null);
        _mockStackClientProvider = new Mock<ILlamaStackRuntimeClientProvider>();
        _mockStackClientProvider
            .Setup(p => p.GetClientForStack(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(_mockLlamaClient.Object);
        _mockLogger = new Mock<ILogger<NotebookModelRuntimeService>>();
        _cache = new MemoryCache(new MemoryCacheOptions());

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);

        _service = CreateService(_cache);
    }

    [TestCleanup]
    public void Cleanup()
    {
        ResetOperationState();
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _cache.Dispose();
    }

    private static void ResetOperationState()
    {
        var operationsField = typeof(NotebookModelRuntimeService)
            .GetField("_operations", BindingFlags.NonPublic | BindingFlags.Static);
        if (operationsField?.GetValue(null) is ConcurrentDictionary<string, ModelLoadOperationDto> operations)
        {
            operations.Clear();
        }
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_NoLocalModels_ReturnsReady()
    {
        // Arrange
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "gpt-4" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };
        
        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);
        
        var model = new Model { ModelId = "gpt-4", Provider = "openai-chat", IsActive = true };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        // Act
        var status = await _service.GetRuntimeStatusAsync(notebookId);

        // Assert
        Assert.AreEqual("ready", status.State);
        Assert.AreEqual(0, status.RequiredModels.Count);
        
        // Verify ListModelsAsync is never called because there are no local models
        _mockLlamaClient.Verify(c => c.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_LocalModelNotLoaded_ReturnsRequiresLoad()
    {
        // Arrange
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };
        
        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);
        
        var model = new Model 
        { 
            ModelId = "qwen-local", 
            Provider = "llama-cpp", 
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse { Data = new List<LlamaModelData>() });

        // Act
        var status = await _service.GetRuntimeStatusAsync(notebookId);

        // Assert
        Assert.AreEqual("requires_load", status.State);
        Assert.AreEqual(1, status.RequiredModels.Count);
        Assert.AreEqual(0, status.LoadedModels.Count);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_LocalModelLoaded_ReturnsReady()
    {
        // Arrange
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };
        
        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);
        
        var model = new Model 
        { 
            ModelId = "qwen-local", 
            Provider = "llama-cpp", 
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse { 
                Data = new List<LlamaModelData> { new LlamaModelData { Id = "qwen-model", Status = new LlamaModelStatus { Value = "loaded" } } } 
            });

        // Act
        var status = await _service.GetRuntimeStatusAsync(notebookId);

        // Assert
        Assert.AreEqual("ready", status.State);
        Assert.AreEqual(1, status.RequiredModels.Count);
        Assert.AreEqual(1, status.LoadedModels.Count);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_RouterModelLoading_ReturnsLoadingWithExternalOperation()
    {
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new() { Id = "qwen-model", Status = new LlamaModelStatus { Value = "loading" } }
                }
            });

        var status = await _service.GetRuntimeStatusAsync(notebookId);

        Assert.AreEqual("loading", status.State);
        Assert.IsNotNull(status.ActiveOperation);
        Assert.AreEqual(NotebookModelRuntimeService.ExternalLoadingOperationId, status.ActiveOperation!.OperationId);
        Assert.AreEqual("loading", status.ActiveOperation.State);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_RequiredRouterModelFailed_ReturnsFailedEvenDuringWarmup()
    {
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLocalAiWarmupService.Setup(s => s.IsWarmupInProgress).Returns(true);
        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new()
                    {
                        Id = "qwen-model",
                        Status = new LlamaModelStatus { Value = "unloaded" },
                        Failed = true,
                        ExitCode = 1
                    }
                }
            });

        var status = await _service.GetRuntimeStatusAsync(notebookId);

        Assert.AreEqual("failed", status.State);
        Assert.IsTrue(status.Conflicts.Any(c => c.Contains("exit code 1", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("failed", status.ActiveOperation?.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(status.ActiveOperation?.ErrorDetails));
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_StartupWarmupInProgress_ReturnsLoadingWithExternalOperation()
    {
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLocalAiWarmupService.Setup(s => s.IsWarmupInProgress).Returns(true);
        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse { Data = new List<LlamaModelData>() });

        var status = await _service.GetRuntimeStatusAsync(notebookId);

        Assert.AreEqual("loading", status.State);
        Assert.AreEqual(NotebookModelRuntimeService.ExternalLoadingOperationId, status.ActiveOperation?.OperationId);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_StartupWarmupInProgressButRequiredModelLoaded_ReturnsReady()
    {
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLocalAiWarmupService.Setup(s => s.IsWarmupInProgress).Returns(true);
        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new()
                    {
                        Id = "qwen-model",
                        Status = new LlamaModelStatus { Value = "loaded" }
                    }
                }
            });

        var status = await _service.GetRuntimeStatusAsync(notebookId);

        Assert.AreEqual("ready", status.State);
        Assert.IsNull(status.ActiveOperation);
    }

    [TestMethod]
    public async Task StartLoadOperationAsync_WhenExternalLoadInProgress_ReturnsExistingOperation()
    {
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLocalAiWarmupService.Setup(s => s.IsWarmupInProgress).Returns(true);
        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse { Data = new List<LlamaModelData>() });

        var op = await _service.StartLoadOperationAsync(notebookId);

        Assert.AreEqual("loading", op.State);
        Assert.AreEqual(NotebookModelRuntimeService.ExternalLoadingOperationId, op.OperationId);
        _mockLocalAiWarmupService.Verify(s => s.UnloadAuxiliaryServicesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_AlwaysUsesFreshRouterSnapshot_ForRepeatedChecks()
    {
        // Arrange
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new() { Id = "qwen-model", Status = new LlamaModelStatus { Value = "loaded" } }
                }
            });

        // Act
        var first = await _service.GetRuntimeStatusAsync(notebookId);
        var second = await _service.GetRuntimeStatusAsync(notebookId);

        // Assert
        Assert.AreEqual("ready", first.State);
        Assert.AreEqual("ready", second.State);
        _mockLlamaClient.Verify(c => c.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_RequiredModelLoaded_DoesNotReportLoadingFromActiveOperation()
    {
        // Arrange
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        // Simulate an in-flight op that can lag behind true model readiness.
        var operationsField = typeof(NotebookModelRuntimeService)
            .GetField("_operations", BindingFlags.NonPublic | BindingFlags.Static);
        if (operationsField?.GetValue(null) is ConcurrentDictionary<string, ModelLoadOperationDto> operations)
        {
            operations["stale-loading-op"] = new ModelLoadOperationDto
            {
                OperationId = "stale-loading-op",
                State = "loading",
                StartedAt = DateTime.UtcNow.AddMinutes(-5)
            };
        }
        else
        {
            Assert.Fail("Unable to seed runtime operation state.");
        }

        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new() { Id = "qwen-model", Status = new LlamaModelStatus { Value = "loaded" } }
                }
            });

        // Act
        var status = await _service.GetRuntimeStatusAsync(notebookId);

        // Assert
        Assert.AreEqual("ready", status.State);
        Assert.IsNull(status.ActiveOperation);
    }

    [TestMethod]
    public async Task GetRuntimeStatusAsync_WithSizeLimitedCache_DoesNotThrowAndUsesFreshState()
    {
        // Arrange
        var limitedCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        var serviceWithLimitedCache = CreateService(limitedCache);

        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        var model = new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        };
        _context.Models.Add(model);
        await _context.SaveChangesAsync();

        _mockLlamaClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new() { Id = "qwen-model", Status = new LlamaModelStatus { Value = "loaded" } }
                }
            });

        try
        {
            // Act
            var first = await serviceWithLimitedCache.GetRuntimeStatusAsync(notebookId);
            var second = await serviceWithLimitedCache.GetRuntimeStatusAsync(notebookId);

            // Assert
            Assert.AreEqual("ready", first.State);
            Assert.AreEqual("ready", second.State);
            _mockLlamaClient.Verify(c => c.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            limitedCache.Dispose();
        }
    }


    private NotebookModelRuntimeService CreateService(IMemoryCache cache)
    {
        return new NotebookModelRuntimeService(
            _context,
            _mockLlamaClient.Object,
            cache,
            new LlamaRuntimeCoordinator(),
            _mockChatModelResolver.Object,
            _mockLocalAiWarmupService.Object,
            _mockLocalAiWarmup.Object,
            new NotebookChatAliasState(),
            _mockStackHostResolver.Object,
            _mockStackClientProvider.Object,
            new StubScopeFactory(),
            _mockLogger.Object);
    }

    [TestMethod]
    public async Task StartLoadOperationAsync_LoadsDirectly_OnOwningInstance_WithoutAnyPlanApply()
    {
        // Contract: llama actuation on a load path is API-direct. The load loads the
        // required alias on its owning instance and performs NO lifecycle-plan apply -
        // the plan carries no llama section, so any apply would emit llama unload
        // intent that the container would act on (the 2026-09-20 defect: a plan apply
        // unloaded a model that was in use).
        // Arrange
        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "qwen-local" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };

        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);
        _context.Models.Add(new Model
        {
            ModelId = "qwen-local",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\"}"
        });
        await _context.SaveChangesAsync();

        var loaded = false;
        _mockLlamaClient
            .Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
                loaded
                    ? new LlamaModelsResponse
                    {
                        Data = new List<LlamaModelData>
                        {
                            new() { Id = "qwen-model", Status = new LlamaModelStatus { Value = "loaded" } }
                        }
                    }
                    : new LlamaModelsResponse { Data = new List<LlamaModelData>() });
        _mockLlamaClient
            .Setup(c => c.LoadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((id, _) =>
            {
                Assert.AreEqual("qwen-model", id, "the load path must load the required alias");
                loaded = true;
            })
            .Returns(Task.CompletedTask);

        // Act
        var op = await _service.StartLoadOperationAsync(notebookId);

        ModelLoadOperationDto? final = null;
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeoutAt)
        {
            final = await _service.GetOperationStatusAsync(notebookId, op.OperationId);
            if (final is not null && (final.State == "ready" || final.State == "failed"))
            {
                break;
            }

            await Task.Delay(50);
        }

        // Assert
        Assert.IsNotNull(final);
        Assert.AreEqual("ready", final!.State, final.ErrorDetails);
        Assert.IsTrue(loaded, "llama load must go through the direct runtime client");

        _mockLlamaClient.Verify(
            c => c.LoadModelAsync("qwen-model", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockLlamaClient.Verify(
            c => c.UnloadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No lifecycle-plan apply on a load path - ever.
        _mockLocalAiWarmup.Verify(
            s => s.SyncDesiredAndApplyAsync(
                It.IsAny<WarmupDesiredBuildOptions?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task StartLoadOperationAsync_RowOwnedInstance_LoadsOnlyThatInstance_OtherInstancesUntouched()
    {
        // Regression: the 2026-09-20 defect where a notebook load on the Max instance
        // (via the plan) unloaded the global instance's default. With per-instance
        // routing the load must touch only the instance that owns the required row.
        var maxBase = "http://192.0.2.1:8112";
        var maxLoaded = false;
        var maxClient = new Mock<ILlamaServerRuntimeClient>();
        maxClient
            .Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
                new LlamaModelsResponse
                {
                    Data = maxLoaded
                        ? new List<LlamaModelData>
                        {
                            new() { Id = "qwen-max", Status = new LlamaModelStatus { Value = "loaded" } }
                        }
                        : new List<LlamaModelData>()
                });
        maxClient
            .Setup(c => c.LoadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, _) => maxLoaded = true)
            .Returns(Task.CompletedTask);

        _mockStackHostResolver
            .Setup(r => r.GetAllConfiguredStackBases())
            .Returns(new List<string> { "http://localhost:8080", maxBase });
        _mockStackHostResolver
            .Setup(r => r.GetAllConfiguredStackBasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "http://localhost:8080", maxBase });
        _mockStackHostResolver
            .Setup(r => r.GetAllConfiguredInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalAiInstance>
            {
                new(LocalAiStackHostResolver.CanonicalInstanceKey("http://localhost:8080"), "http://localhost:8080"),
                new(LocalAiStackHostResolver.CanonicalInstanceKey(maxBase), maxBase)
            });
        _mockStackClientProvider
            .Setup(p => p.GetClientForStack(maxBase, It.IsAny<string?>()))
            .Returns(maxClient.Object);

        var notebookId = Guid.NewGuid();
        var guide = new Assistant { Id = Guid.NewGuid(), Kind = AssistantKind.Guide, ModelId = "max-model" };
        var notebook = new Notebook { Id = notebookId, GuideId = guide.Id, Guide = guide };
        _context.Assistants.Add(guide);
        _context.Notebooks.Add(notebook);

        // The global instance has its own default loaded; it must not be touched.
        _mockLlamaClient
            .Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaModelsResponse
            {
                Data = new List<LlamaModelData>
                {
                    new() { Id = "qwen-default", Status = new LlamaModelStatus { Value = "loaded" } }
                }
            });

        _context.Models.Add(new Model
        {
            ModelId = "max-model",
            Provider = "llama-cpp",
            IsActive = true,
            RuntimeConfigJson =
                "{\"routerModelId\":\"qwen-max\",\"stackBaseUrl\":\"" + maxBase + "\"}"
        });
        await _context.SaveChangesAsync();

        var op = await _service.StartLoadOperationAsync(notebookId);

        ModelLoadOperationDto? final = null;
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeoutAt)
        {
            final = await _service.GetOperationStatusAsync(notebookId, op.OperationId);
            if (final is not null && (final.State == "ready" || final.State == "failed"))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.IsNotNull(final);
        Assert.AreEqual("ready", final!.State, final.ErrorDetails);

        // The row-owned instance loaded its model...
        maxClient.Verify(c => c.LoadModelAsync("qwen-max", It.IsAny<CancellationToken>()), Times.Once);
        // ...and the global instance's client was never asked to load or unload anything.
        _mockLlamaClient.Verify(c => c.LoadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLlamaClient.Verify(c => c.UnloadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Minimal scope factory that resolves nothing (no row-owned DB reads).</summary>
    private sealed class StubScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new StubScope();
    }

    private sealed class StubScope : Microsoft.Extensions.DependencyInjection.IServiceScope, IDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider =
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection());
        public IServiceProvider ServiceProvider => _provider;
        public void Dispose() => _provider.Dispose();
    }
}