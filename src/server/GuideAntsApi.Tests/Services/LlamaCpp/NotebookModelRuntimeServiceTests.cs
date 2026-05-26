using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public class NotebookModelRuntimeServiceTests
{
    private Mock<ILlamaServerRuntimeClient> _mockLlamaClient = null!;
    private Mock<IRuntimeProfileResolver> _mockRuntimeProfileResolver = null!;
    private Mock<IChatModelResolver> _mockChatModelResolver = null!;
    private Mock<ILogger<NotebookModelRuntimeService>> _mockLogger = null!;
    private IMemoryCache _cache = null!;
    private ApplicationDbContext _context = null!;
    private NotebookModelRuntimeService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLlamaClient = new Mock<ILlamaServerRuntimeClient>();
        _mockRuntimeProfileResolver = new Mock<IRuntimeProfileResolver>();
        _mockChatModelResolver = new Mock<IChatModelResolver>();
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
            _mockLogger.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _cache.Dispose();
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
}

