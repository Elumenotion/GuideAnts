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
    private Mock<IRouterModelsConfigService> _router = null!;

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
        _router = new Mock<IRouterModelsConfigService>();
    }

    [TestCleanup]
    public void Cleanup() => _db.Dispose();

    private ConversationContextStatusService Build() =>
        new(new TestServiceScopeFactory(_db), _resolver.Object, _router.Object,
            NullLogger<ConversationContextStatusService>.Instance);

    private ConversationTurn Turn(
        int index, int? lastRoundTokens, int? lastRoundChars, string status = "completed", string model = ModelId) =>
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
                        LastRoundPromptTokens = lastRoundTokens,
                        LastRoundPromptChars = lastRoundChars
                    },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        };

    private NotebookConversationMessage Msg(int turn, int seq, DataModelChatRole role, int chars) =>
        new()
        {
            NotebookConversationId = _conversationId,
            TurnIndex = turn,
            MessageSequence = seq,
            Role = role,
            Content = new string('x', chars)
        };

    private async Task Seed(IEnumerable<ConversationTurn> turns, IEnumerable<NotebookConversationMessage>? messages = null)
    {
        _db.ConversationTurns.AddRange(turns);
        if (messages != null)
        {
            _db.NotebookConversationMessages.AddRange(messages);
        }

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
    public async Task ProviderUsage_IsUsedAsTheBaseEstimate()
    {
        await Seed([Turn(1, 10_000, 40_000)], [Msg(1, 2, DataModelChatRole.Assistant, 400)]);

        var result = await Build().GetAsync(_conversationId);

        // Calibration: 40_000 chars / 10_000 tokens = 4.0.
        result.EstimatedPromptTokens.Should().Be(10_100);
        result.EstimateSource.Should().Be(ContextEstimateSource.ProviderUsage);
        result.ContextWindowTokens.Should().Be(128_000);
        result.ModelDeploymentId.Should().Be(ModelId);
        result.ContextWindowSource.Should().Be(ContextWindowSource.Catalog);
    }

    [TestMethod]
    public async Task ProviderUsage_AddsMessagesFromLaterTurns()
    {
        await Seed(
            [Turn(1, 10_000, 40_000), Turn(2, null, null)],
            [
                Msg(1, 1, DataModelChatRole.User, 999),
                Msg(1, 2, DataModelChatRole.Assistant, 400),
                Msg(2, 1, DataModelChatRole.User, 300),
                Msg(2, 2, DataModelChatRole.Assistant, 500)
            ]);

        var result = await Build().GetAsync(_conversationId);

        // (400 assistant-of-turn-1 + 800 later) / 4.0 = 300; the turn-1 user message is already in the provider count.
        result.EstimatedPromptTokens.Should().Be(10_300);
        result.EstimateSource.Should().Be(ContextEstimateSource.ProviderUsage);
    }

    [TestMethod]
    public async Task NoProviderUsage_FallsBackToCharacterEstimate()
    {
        await Seed(
            [Turn(1, null, null)],
            [Msg(1, 1, DataModelChatRole.User, 1_000), Msg(1, 2, DataModelChatRole.Assistant, 3_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().Be(1_000);
        result.EstimateSource.Should().Be(ContextEstimateSource.Characters);
    }

    [TestMethod]
    public async Task CharacterFallback_WithACompactionBoundary_OnlyCountsMessagesAfterTheBoundary()
    {
        // No completed turn has usage data (e.g. a local model that doesn't report usage), so the
        // Characters fallback is the only estimate available. The conversation was compacted at
        // turn 1: only turn 2's 100 chars should count, not turn 1's 9,000 pre-boundary chars.
        var conversation = await _db.NotebookConversations.SingleAsync(c => c.Id == _conversationId);
        conversation.CompactionBoundaryTurnIndex = 1;
        await _db.SaveChangesAsync();
        await Seed(
            [Turn(1, null, null), Turn(2, null, null)],
            [
                Msg(1, 1, DataModelChatRole.User, 4_000),
                Msg(1, 2, DataModelChatRole.Assistant, 5_000),
                Msg(2, 1, DataModelChatRole.User, 100)
            ]);

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().Be(25,
            "the estimate must reflect what the model would actually see -- the summary plus the tail -- " +
            "not the full pre-boundary history the compaction was meant to shrink");
        result.EstimateSource.Should().Be(ContextEstimateSource.Characters);
        result.BoundaryTurnIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task Calibration_ShiftsTheCharacterEstimate()
    {
        // Turn 1 observed ratio 2.0 but is superseded as baseline by nothing: give it the usage, then
        // a later turn with none. Baseline path uses 2.0 for the trailing chars.
        await Seed(
            [Turn(1, 1_000, 2_000), Turn(2, null, null)],
            [Msg(2, 1, DataModelChatRole.User, 2_000)]);

        var result = await Build().GetAsync(_conversationId);

        // 1_000 provider + 2_000 chars / 2.0 = 2_000 total (would be 1_500 at the default 4.0).
        result.EstimatedPromptTokens.Should().Be(2_000);
    }

    [TestMethod]
    public async Task IgnoresIncompleteTurns()
    {
        await Seed(
            [Turn(1, 5_000, 20_000), Turn(2, 99_999, 400_000, status: "streaming")]);

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().Be(5_000);
    }

    [TestMethod]
    public async Task UnknownWindow_ReturnsNullWindowButKeepsTheEstimate()
    {
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<int?>()))
            .Returns(new ContextWindowInfo(null, null, ContextWindowSource.Unknown));
        await Seed([Turn(1, 10_000, 40_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.ContextWindowTokens.Should().BeNull();
        result.EstimatedPromptTokens.Should().Be(10_000);
    }

    [TestMethod]
    public async Task ModelIdPassedToResolver_IsTheNewestCompletedTurnsModelDeploymentId()
    {
        // Pins the id space: turns persist the IChatModelResolver-resolved catalog id.
        await Seed([Turn(1, 1_000, 4_000, model: "old-model"), Turn(2, 1_000, 4_000, model: "resolved-id")]);

        await Build().GetAsync(_conversationId);

        _resolver.Verify(r => r.Resolve("resolved-id", null), Times.Once);
    }

    [TestMethod]
    public async Task LocalModel_PassesRouterContextSizeAsLiveRuntimeValue()
    {
        _db.Models.Add(new Model
        {
            ModelId = ModelId,
            DisplayName = "Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = "{\"routerModelId\":\"qwen\"}"
        });
        await Seed([Turn(1, 1_000, 4_000)]);
        _router.Setup(r => r.GetEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RouterModelEntry("qwen", "/m/qwen.gguf", "", ContextSize: 8_192)]);

        _resolver.Setup(r => r.Resolve(ModelId, 8_192))
            .Returns(new ContextWindowInfo(8_192, null, ContextWindowSource.LiveRuntime));

        var result = await Build().GetAsync(_conversationId);

        result.ContextWindowSource.Should().Be(ContextWindowSource.LiveRuntime);
        result.ContextWindowTokens.Should().Be(8_192);
        result.ModelDeploymentId.Should().Be(ModelId);
        _resolver.Verify(r => r.Resolve(ModelId, 8_192), Times.Once);
    }

    [TestMethod]
    public async Task RouterLookupFailure_DegradesToNullLive()
    {
        _db.Models.Add(new Model
        {
            ModelId = ModelId,
            DisplayName = "Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = "{\"routerModelId\":\"qwen\"}"
        });
        await Seed([Turn(1, 1_000, 4_000)]);
        _router.Setup(r => r.GetEntriesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("admin down"));

        var result = await Build().GetAsync(_conversationId);

        result.EstimatedPromptTokens.Should().Be(1_000);
        _resolver.Verify(r => r.Resolve(ModelId, null), Times.Once);
    }

    [TestMethod]
    public async Task BoundaryTurnIndex_ReflectsThePersistedColumn()
    {
        _db.NotebookConversations.Single().CompactionBoundaryTurnIndex = 3;
        await _db.SaveChangesAsync();
        await Seed([Turn(1, 1_000, 4_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.BoundaryTurnIndex.Should().Be(3);
    }

    [TestMethod]
    public async Task BoundaryTurnIndex_IsNull_WhenConversationNeverCompacted()
    {
        await Seed([Turn(1, 1_000, 4_000)]);

        var result = await Build().GetAsync(_conversationId);

        result.BoundaryTurnIndex.Should().BeNull();
    }
}
