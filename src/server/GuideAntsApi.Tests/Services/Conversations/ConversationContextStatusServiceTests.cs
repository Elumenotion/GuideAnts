using System.Text.Json;
using AntRunner.Chat;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationContextStatusServiceTests
{
    private const string ModelId = "test-model";

    private ApplicationDbContext _db = null!;
    private Guid _conversationId;
    private Mock<IContextWindowResolver> _resolver = null!;
    private Mock<ILlamaStackRuntimeClientProvider> _runtimeClients = null!;
    private Mock<ILlamaServerRuntimeClient> _runtimeClient = null!;

    [TestInitialize]
    public void Setup()
    {
        _db = new ApplicationDbContext(BackgroundJobTestHelpers.CreateInMemoryOptions($"status-{Guid.NewGuid():N}"));
        _conversationId = Guid.NewGuid();
        var notebook = new Notebook { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "NB" };
        _db.Notebooks.Add(notebook);
        _db.NotebookConversations.Add(new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = notebook.Id,
            Title = "Convo",
            Notebook = notebook
        });
        _db.SaveChanges();

        _resolver = new Mock<IContextWindowResolver>();
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()))
            .Returns(new ContextWindowInfo(128_000, null, ContextWindowSource.Catalog));

        _runtimeClient = new Mock<ILlamaServerRuntimeClient>();
        _runtimeClients = new Mock<ILlamaStackRuntimeClientProvider>();
        _runtimeClients.Setup(p => p.GetClientForStack(null, null)).Returns((ILlamaServerRuntimeClient?)null);
        _runtimeClients.Setup(p => p.Global).Returns(_runtimeClient.Object);
    }

    [TestCleanup]
    public void Cleanup() => _db.Dispose();

    private ConversationContextStatusService Build() =>
        new(new TestServiceScopeFactory(_db), _resolver.Object, _runtimeClients.Object,
            NullLogger<ConversationContextStatusService>.Instance);

    private ConversationTurn Turn(
        int index, int? lastRoundTokens, string status = "completed", string model = ModelId) =>
        new()
        {
            NotebookConversationId = _conversationId,
            TurnIndex = index,
            AssistantName = "a",
            ModelDeploymentId = model,
            Instructions = "i",
            Status = status,
            UsageJson = lastRoundTokens is null
                ? null
                : JsonSerializer.Serialize(
                    new UsageResponse
                    {
                        PromptTokens = lastRoundTokens,
                        LastRoundPromptTokens = lastRoundTokens
                    },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        };

    private LlamaModelsResponse ModelsResponse(string modelId, string status, int? nCtx) =>
        new()
        {
            Data =
            {
                new LlamaModelData
                {
                    Id = modelId,
                    Status = new LlamaModelStatus { Value = status },
                    Meta = new LlamaModelMeta { NCtx = nCtx }
                }
            }
        };

    private async Task Seed(IEnumerable<ConversationTurn> turns)
    {
        _db.ConversationTurns.AddRange(turns);
        await _db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task NoCompletedTurn_ReturnsNoneAndNulls()
    {
        var result = await Build().GetAsync(_conversationId);

        result.ContextWindowTokens.Should().BeNull();
        result.EstimatedPromptTokens.Should().BeNull();
        result.EstimateSource.Should().Be(ContextEstimateSource.None);
        result.ModelDeploymentId.Should().BeNull();
        result.ContextWindowSource.Should().Be(ContextWindowSource.Unknown);
        _resolver.Verify(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [TestMethod]
    public async Task Tokens_AreTheNewestCompletedTurnsProviderReportedCount()
    {
        await Seed([Turn(1, 10_000), Turn(2, 12_500)]);

        var result = await Build().GetAsync(_conversationId);

        // No char-based estimation: the real provider number of the newest completed turn.
        result.EstimatedPromptTokens.Should().Be(12_500);
        result.EstimateSource.Should().Be(ContextEstimateSource.ProviderUsage);
        result.ContextWindowTokens.Should().Be(128_000);
        result.ModelDeploymentId.Should().Be(ModelId);
        result.ContextWindowSource.Should().Be(ContextWindowSource.Catalog);
    }

    [TestMethod]
    public async Task Tokens_AreNull_WhenNoCompletedTurnReportsUsage()
    {
        await Seed([Turn(1, null)]);

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().BeNull();
        result.EstimateSource.Should().Be(ContextEstimateSource.None);
    }

    [TestMethod]
    public async Task Tokens_SkipTurnsMissingLastRoundCount()
    {
        // Turn 2 (newest) has no usage; turn 1's real count is what the meter reports.
        await Seed([Turn(1, 10_000), Turn(2, null)]);

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().Be(10_000);
        result.EstimateSource.Should().Be(ContextEstimateSource.ProviderUsage);
    }

    [TestMethod]
    public async Task IgnoresIncompleteTurns()
    {
        await Seed([Turn(1, 5_000), Turn(2, 99_999, status: "streaming")]);

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().Be(5_000);
    }

    [TestMethod]
    public async Task UnknownWindow_ReturnsNullWindowButKeepsTokens()
    {
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()))
            .Returns(new ContextWindowInfo(null, null, ContextWindowSource.Unknown));
        await Seed([Turn(1, 10_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.ContextWindowTokens.Should().BeNull();
        result.EstimatedPromptTokens.Should().Be(10_000);
    }

    [TestMethod]
    public async Task ModelIdPassedToResolver_IsTheNewestCompletedTurnsModelDeploymentId()
    {
        await Seed([Turn(1, 1_000, model: "old-model"), Turn(2, 1_000, model: "resolved-id")]);

        await Build().GetAsync(_conversationId);

        _resolver.Verify(r => r.Resolve("resolved-id", null), Times.Once);
    }

    [TestMethod]
    public async Task LocalModel_WindowComesFromTheLoadedModelsServerReportedContext()
    {
        _db.Models.Add(new Model
        {
            ModelId = ModelId,
            DisplayName = "Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = "{\"routerModelId\":\"qwen\"}"
        });
        await Seed([Turn(1, 1_000)]);
        _runtimeClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ModelsResponse("qwen", "loaded", 8_192));

        var result = await Build().GetAsync(_conversationId);

        result.ContextWindowSource.Should().Be(ContextWindowSource.LiveRuntime);
        result.ContextWindowTokens.Should().Be(8_192);
        result.ModelDeploymentId.Should().Be(ModelId);
        // The loaded model's server is the only window source for llama rows: no resolver call.
        _resolver.Verify(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [TestMethod]
    public async Task LocalModel_UnloadedModelHasNoLiveWindow()
    {
        _db.Models.Add(new Model
        {
            ModelId = ModelId,
            DisplayName = "Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = "{\"routerModelId\":\"qwen\"}"
        });
        await Seed([Turn(1, 1_000)]);
        _runtimeClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ModelsResponse("qwen", "unloaded", null));

        var result = await Build().GetAsync(_conversationId);

        result.ContextWindowTokens.Should().BeNull();
        result.ContextWindowSource.Should().Be(ContextWindowSource.Unknown);
        result.EstimatedPromptTokens.Should().Be(1_000);
        _resolver.Verify(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [TestMethod]
    public async Task LocalModel_RuntimeQueryFailure_WindowStaysNull()
    {
        _db.Models.Add(new Model
        {
            ModelId = ModelId,
            DisplayName = "Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = "{\"routerModelId\":\"qwen\"}"
        });
        await Seed([Turn(1, 1_000)]);
        _runtimeClient.Setup(c => c.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server down"));

        var result = await Build().GetAsync(_conversationId);

        // The model's server is the only window source for llama rows: a failed query means an
        // unknown window, never a catalog/learned substitute. Tokens are unaffected.
        result.ContextWindowTokens.Should().BeNull();
        result.ContextWindowSource.Should().Be(ContextWindowSource.Unknown);
        result.EstimatedPromptTokens.Should().Be(1_000);
        _resolver.Verify(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [TestMethod]
    public async Task NonLlamaModel_NeverQueriesTheLlamaRuntime()
    {
        _db.Models.Add(new Model
        {
            ModelId = ModelId,
            DisplayName = "Cloud",
            Provider = "openrouter-chat",
            RuntimeConfigJson = null
        });
        await Seed([Turn(1, 1_000)]);

        var result = await Build().GetAsync(_conversationId);

        _runtimeClient.Verify(c => c.ListModelsAsync(It.IsAny<CancellationToken>()), Times.Never);
        result.ContextWindowTokens.Should().Be(128_000);
        result.ContextWindowSource.Should().Be(ContextWindowSource.Catalog);
        // Non-llama rows keep the resolver's catalog/learned chain.
        _resolver.Verify(r => r.Resolve(ModelId, null), Times.Once);
    }

    [TestMethod]
    public async Task BoundaryTurnIndex_ReflectsThePersistedColumn()
    {
        _db.NotebookConversations.Single().CompactionBoundaryTurnIndex = 3;
        await _db.SaveChangesAsync();
        await Seed([Turn(1, 1_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.BoundaryTurnIndex.Should().Be(3);
    }

    [TestMethod]
    public async Task BoundaryTurnIndex_IsNull_WhenConversationNeverCompacted()
    {
        await Seed([Turn(1, 1_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.BoundaryTurnIndex.Should().BeNull();
    }
}
