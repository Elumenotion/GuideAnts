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
    private Mock<IRuntimeProfileResolver> _mockRuntimeProfileResolver = null!;
    private Mock<IChatModelResolver> _mockChatModelResolver = null!;
    private Mock<ILocalAiStartupWarmupService> _mockLocalAiWarmupService = null!;
    private Mock<ILogger<NotebookModelRuntimeService>> _mockLogger = null!;
    private IMemoryCache _cache = null!;
    private ApplicationDbContext _context = null!;
    private NotebookModelRuntimeService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        ResetOperationState();

        _mockLlamaClient = new Mock<ILlamaServerRuntimeClient>();
        _mockRuntimeProfileResolver = new Mock<IRuntimeProfileResolver>();
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
        _mockLogger = new Mock<ILogger<NotebookModelRuntimeService>>();
        _cache = new MemoryCache(new MemoryCacheOptions());

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);

        _service = new NotebookModelRuntimeService(
            _context,
            _mockLlamaClient.Object,
            _mockRuntimeProfileResolver.Object,
            _cache,
            new LlamaRuntimeCoordinator(),
            _mockChatModelResolver.Object,
            _mockLocalAiWarmupService.Object,
            _mockLogger.Object);
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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
        var serviceWithLimitedCache = new NotebookModelRuntimeService(
            _context,
            _mockLlamaClient.Object,
            _mockRuntimeProfileResolver.Object,
            limitedCache,
            new LlamaRuntimeCoordinator(),
            _mockChatModelResolver.Object,
            _mockLocalAiWarmupService.Object,
            _mockLogger.Object);

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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
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

    [TestMethod]
    public async Task StartLoadOperationAsync_UnloadsAuxiliaryServicesBeforeLlmLoad_AndReloadsAfter()
    {
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
            RuntimeConfigJson = "{\"routerModelId\":\"qwen-model\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"qwen-model\"}}"
        });
        await _context.SaveChangesAsync();

        var callOrder = new List<string>();
        _mockLocalAiWarmupService
            .Setup(s => s.UnloadAuxiliaryServicesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("aux-unload"))
            .Returns(Task.CompletedTask);
        _mockLocalAiWarmupService
            .Setup(s => s.EnsureAuxiliaryServicesLoadedAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("aux-reload"))
            .Returns(Task.CompletedTask);

        var listCalls = 0;
        _mockLlamaClient
            .Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                listCalls++;
                return listCalls >= 3
                    ? new LlamaModelsResponse
                    {
                        Data = new List<LlamaModelData>
                        {
                            new LlamaModelData
                            {
                                Id = "qwen-model",
                                Status = new LlamaModelStatus { Value = "loaded" }
                            }
                        }
                    }
                    : new LlamaModelsResponse { Data = new List<LlamaModelData>() };
            });

        _mockLlamaClient
            .Setup(c => c.LoadModelAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.Nodes.JsonObject?>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("llm-load"))
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

        var unloadIndex = callOrder.IndexOf("aux-unload");
        var llmLoadIndex = callOrder.IndexOf("llm-load");
        var reloadIndex = callOrder.IndexOf("aux-reload");
        Assert.IsTrue(unloadIndex >= 0, "aux-unload call missing");
        Assert.IsTrue(llmLoadIndex >= 0, "llm-load call missing");
        Assert.IsTrue(reloadIndex >= 0, "aux-reload call missing");
        Assert.IsTrue(unloadIndex < llmLoadIndex, "aux unload did not happen before llama load");
        Assert.IsTrue(llmLoadIndex < reloadIndex, "aux reload did not happen after llama load");

        _mockLocalAiWarmupService.Verify(s => s.UnloadAuxiliaryServicesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockLocalAiWarmupService.Verify(s => s.EnsureAuxiliaryServicesLoadedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

