using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationPersistenceTerminalizeTests
{
    [TestMethod]
    public async Task TerminalizeTurnAsync_IsIdempotentWhenTurnAlreadyTerminal()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("completed");
        var assistantId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "Final answer.",
                IsStreaming = false,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var output = new ChatRunOutput
        {
            Status = "completed",
            Usage = new UsageResponse { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 }
        };

        var first = await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "completed",
            Output: output));

        var second = await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "failed",
            TerminationCode: "retry",
            Output: output));

        first.Should().BeTrue();
        second.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("completed");
        turn.TerminationCode.Should().BeNull();
        turn.ChatRunOutputJson.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task TerminalizeTurnAsync_FinalizesStreamingAssistantRows()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var assistantId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "streaming partial",
                IsStreaming = true,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "cancelled",
            TerminationCode: "cancelled",
            AssistantSnapshots: [
                new TerminalizeAssistantSnapshot(assistantId, "streaming partial final")
            ],
            PruneIncompleteToolCalls: true));

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("cancelled");
        turn.TerminalizedAt.Should().NotBeNull();

        var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == assistantId);
        assistant.IsStreaming.Should().BeFalse();
        assistant.Content.Should().Be("streaming partial final");
    }

    [TestMethod]
    public async Task RequestTurnCancellationAsync_marks_streaming_turn_without_terminalizing()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");

        var requested = await persistence.RequestTurnCancellationAsync(
            conversationId,
            turnId,
            CancellationToken.None);

        requested.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("streaming");
        turn.TerminationCode.Should().Be("cancel_requested");
        turn.TerminationDetail.Should().Be("Stop was requested by the user.");
    }

    [TestMethod]
    public async Task FenceTurnCancellationAsync_terminalizes_and_releases_only_the_old_lease()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var oldExecutionId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            var turn = await seed.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.ExecutionId = oldExecutionId;
            seed.ConversationLocks.Add(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = oldExecutionId,
                LockedByUserName = "tester",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "durable partial",
                IsStreaming = true,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var result = await persistence.FenceTurnCancellationAsync(
            conversationId,
            turnId,
            oldExecutionId);

        result.Found.Should().BeTrue();
        result.WasStreaming.Should().BeTrue();
        result.PreviousExecutionId.Should().Be(oldExecutionId);
        result.FencedExecutionId.Should().NotBe(oldExecutionId);
        result.PreviousLeaseWasReleased.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var turnAfterStop = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turnAfterStop.Status.Should().Be("cancelled");
        turnAfterStop.TerminationCode.Should().Be("cancel_requested");
        turnAfterStop.ExecutionId.Should().Be(result.FencedExecutionId);

        var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == assistantId);
        assistant.Content.Should().Be("durable partial");
        assistant.IsStreaming.Should().BeFalse();
        (await db.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId))
            .Should()
            .BeFalse();
    }

    [TestMethod]
    public async Task FenceTurnCancellationAsync_cancels_pending_client_tool_and_advances_its_fence()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("pending_client_tool");
        var oldExecutionId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            var turn = await seed.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.ExecutionId = oldExecutionId;
            seed.ConversationLocks.Add(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = oldExecutionId,
                LockedByUserName = "tester",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            await seed.SaveChangesAsync();
        }

        var result = await persistence.FenceTurnCancellationAsync(conversationId, turnId);

        result.Found.Should().BeTrue();
        result.WasStreaming.Should().BeFalse();
        result.WasPendingClientTool.Should().BeTrue();
        result.Status.Should().Be("cancelled");
        result.FencedExecutionId.Should().NotBe(oldExecutionId);
        result.PreviousLeaseWasReleased.Should().BeTrue();

        await using var verify = new ApplicationDbContext(_options);
        var turnAfterStop = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turnAfterStop.Status.Should().Be("cancelled");
        turnAfterStop.TerminationCode.Should().Be("cancel_requested");
        (await verify.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId))
            .Should()
            .BeFalse();
    }

    [TestMethod]
    public async Task FenceTurnCancellationAsync_refuses_to_cross_a_newer_execution_lease()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var oldExecutionId = Guid.NewGuid();
        var newerExecutionId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            var turn = await seed.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.ExecutionId = oldExecutionId;
            seed.ConversationLocks.Add(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = newerExecutionId,
                LockedByUserName = "new-owner",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            await seed.SaveChangesAsync();
        }

        var result = await persistence.FenceTurnCancellationAsync(conversationId, turnId);

        result.Found.Should().BeTrue();
        result.ConflictingLeasePresent.Should().BeTrue();
        result.Status.Should().Be("streaming");
        result.FencedExecutionId.Should().Be(oldExecutionId);

        await using var verify = new ApplicationDbContext(_options);
        var turnAfterStop = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turnAfterStop.Status.Should().Be("streaming");
        turnAfterStop.ExecutionId.Should().Be(oldExecutionId);
        (await verify.ConversationLocks.SingleAsync(l => l.ConversationId == conversationId)).LeaseId
            .Should()
            .Be(newerExecutionId);
    }

    [TestMethod]
    public async Task TerminalizeTurnAsync_preserves_a_durable_cancellation_request()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        await persistence.RequestTurnCancellationAsync(conversationId, turnId);

        var terminalized = await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "completed",
            TerminationCode: "completed",
            Output: new ChatRunOutput { Status = "completed" }));

        terminalized.Should().BeTrue();

        await using var db = new ApplicationDbContext(_options);
        var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("cancelled");
        turn.TerminationCode.Should().Be("cancelled");
    }

    [TestMethod]
    public async Task TerminalizeTurnAsync_rejects_output_from_a_fenced_execution()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("cancelled");
        var currentExecutionId = Guid.NewGuid();
        var staleExecutionId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            var turn = await seed.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.ExecutionId = currentExecutionId;
            turn.ChatRunOutputJson = "durable-cancelled-output";
            await seed.SaveChangesAsync();
        }

        var terminalized = await persistence.TerminalizeTurnAsync(new TerminalizeTurnRequest(
            turnId,
            conversationId,
            TurnIndex: 1,
            TerminalStatus: "completed",
            TerminationCode: "completed",
            ExecutionId: staleExecutionId,
            Output: new ChatRunOutput { Status = "completed" }));

        terminalized.Should().BeFalse();

        await using var verify = new ApplicationDbContext(_options);
        var turnAfterAttempt = await verify.ConversationTurns.SingleAsync(t => t.Id == turnId);
        turnAfterAttempt.Status.Should().Be("cancelled");
        turnAfterAttempt.ExecutionId.Should().Be(currentExecutionId);
        turnAfterAttempt.ChatRunOutputJson.Should().Be("durable-cancelled-output");
    }

    [TestMethod]
    public async Task MaterializeMissingCancellationToolResultsAsync_creates_tool_results_for_unmatched_calls()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var assistantId = Guid.NewGuid();
        const string toolCallsJson =
            """[{"id":"call_stop_1","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "Re-running the fix and test:",
                ToolCalls = toolCallsJson,
                ThinkingBlocksJson = """[{"type":"thinking","thinking":"partial reasoning","signature":""}]""",
                IsStreaming = false,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var materialized = await persistence.MaterializeMissingCancellationToolResultsAsync(
            conversationId,
            turnId);

        materialized.Should().Be(1);

        await using var db = new ApplicationDbContext(_options);
        var toolMessage = await db.NotebookConversationMessages.SingleAsync(m =>
            m.Role == DataModelChatRole.Tool && m.ToolCallId == "call_stop_1");
        toolMessage.Content.Should().Be("ERROR: Operation was cancelled");
        toolMessage.FunctionName.Should().Be("run_python");
    }

    [TestMethod]
    public async Task MaterializeMissingCancellationToolResultsAsync_is_idempotent()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("cancelled");
        var assistantId = Guid.NewGuid();
        const string toolCallsJson =
            """[{"id":"call_stop_2","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "Working...",
                ToolCalls = toolCallsJson,
                IsStreaming = false,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        (await persistence.MaterializeMissingCancellationToolResultsAsync(conversationId, turnId))
            .Should()
            .Be(1);
        (await persistence.MaterializeMissingCancellationToolResultsAsync(conversationId, turnId))
            .Should()
            .Be(0);

        await using var db = new ApplicationDbContext(_options);
        (await db.NotebookConversationMessages.CountAsync(m => m.ToolCallId == "call_stop_2"))
            .Should()
            .Be(1);
    }

    [TestMethod]
    public async Task FenceTurnCancellationAsync_materializes_tool_results_for_announced_calls()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var assistantId = Guid.NewGuid();
        const string toolCallsJson =
            """[{"id":"call_fence_1","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                Content = "Running tool:",
                ToolCalls = toolCallsJson,
                ThinkingBlocksJson = """[{"type":"thinking","thinking":"thinking before stop","signature":""}]""",
                IsStreaming = true,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var result = await persistence.FenceTurnCancellationAsync(conversationId, turnId);

        result.Found.Should().BeTrue();
        result.Status.Should().Be("cancelled");

        await using var db = new ApplicationDbContext(_options);
        var toolMessage = await db.NotebookConversationMessages.SingleAsync(m =>
            m.Role == DataModelChatRole.Tool && m.ToolCallId == "call_fence_1");
        toolMessage.Content.Should().Be("ERROR: Operation was cancelled");

        var assistant = await db.NotebookConversationMessages.SingleAsync(m => m.Id == assistantId);
        assistant.IsStreaming.Should().BeFalse();
        assistant.ThinkingBlocksJson.Should().Contain("thinking before stop");
    }

    [TestMethod]
    public async Task Streaming_writes_are_rejected_after_execution_fence_changes()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("streaming");
        var currentExecutionId = Guid.NewGuid();
        var staleExecutionId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(_options))
        {
            var turn = await db.ConversationTurns.SingleAsync(t => t.Id == turnId);
            turn.ExecutionId = currentExecutionId;
            await db.SaveChangesAsync();
        }

        var act = () => persistence.StartAssistantMessageAsync(new StartAssistantMessageRequest(
            conversationId,
            turnId,
            TurnIndex: 1,
            MessageSequence: 2,
            AssistantName: "Guide",
            ModelDeploymentId: "test",
            AssistantId: Guid.NewGuid(),
            ExpectedExecutionId: staleExecutionId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no longer allowed*");

        await using var verify = new ApplicationDbContext(_options);
        (await verify.NotebookConversationMessages.CountAsync()).Should().Be(0);
        (await verify.ConversationTurns.SingleAsync(t => t.Id == turnId)).Status.Should().Be("streaming");
    }

    [TestMethod]
    public async Task TryPreserveStoppedAssistantToolCallsAsync_creates_missing_row_after_start_was_fenced()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("cancelled");
        const string toolCallsJson =
            """[{"id":"call_fenced_1","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";

        var preserved = await persistence.TryPreserveStoppedAssistantToolCallsAsync(
            conversationId,
            turnId,
            messageId: null,
            content: "Running the tool.",
            toolCallsJson,
            assistantId: Guid.NewGuid());

        preserved.Should().BeTrue();

        await using var verify = new ApplicationDbContext(_options);
        var assistant = await verify.NotebookConversationMessages
            .SingleAsync(m => m.Role == DataModelChatRole.Assistant);
        assistant.Content.Should().Be("Running the tool.");
        assistant.ToolCalls.Should().Be(toolCallsJson);
        assistant.IsStreaming.Should().BeFalse();

        (await persistence.TryPreserveStoppedAssistantToolCallsAsync(
            conversationId,
            turnId,
            messageId: null,
            content: "Running the tool.",
            toolCallsJson,
            assistantId: Guid.NewGuid()))
            .Should()
            .BeFalse();
    }

    [TestMethod]
    public async Task TryPreserveStoppedAssistantToolCallsAsync_appends_row_instead_of_overwriting_prior_calls()
    {
        var (persistence, conversationId, turnId) = await SeedAsync("cancelled");
        var firstAssistantId = Guid.NewGuid();
        const string firstToolCallsJson =
            """[{"id":"call_fenced_1","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";
        const string secondToolCallsJson =
            """[{"id":"call_fenced_2","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = firstAssistantId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                Content = "First tool round.",
                ToolCalls = firstToolCallsJson,
                IsStreaming = false,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var preserved = await persistence.TryPreserveStoppedAssistantToolCallsAsync(
            conversationId,
            turnId,
            firstAssistantId,
            "Second tool round.",
            secondToolCallsJson,
            assistantId: Guid.NewGuid());

        preserved.Should().BeTrue();

        await using var verify = new ApplicationDbContext(_options);
        var assistants = await verify.NotebookConversationMessages
            .Where(m => m.Role == DataModelChatRole.Assistant)
            .OrderBy(m => m.MessageSequence)
            .ToListAsync();
        assistants.Should().HaveCount(2);
        assistants[0].ToolCalls.Should().Be(firstToolCallsJson);
        assistants[1].ToolCalls.Should().Be(secondToolCallsJson);
        assistants.Select(m => m.MessageSequence).Should().OnlyHaveUniqueItems();
    }

    private DbContextOptions<ApplicationDbContext> _options = null!;

    private async Task<(ConversationPersistence Persistence, Guid ConversationId, Guid TurnId)> SeedAsync(string turnStatus)
    {
        _options = BackgroundJobTestHelpers.CreateInMemoryOptions($"terminalize-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(_options))
        {
            seed.Projects.Add(new Project { Id = projectId, Title = "P", Slug = "p", Created = DateTime.UtcNow });
            seed.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "NB",
                Slug = "nb",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Chat",
                Created = DateTime.UtcNow
            });
            seed.ConversationTurns.Add(new ConversationTurn
            {
                Id = turnId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                AssistantName = "Guide",
                Status = turnStatus,
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                TerminalizedAt = turnStatus == "completed" ? DateTime.UtcNow : null
            });
            await seed.SaveChangesAsync();
        }

        var db = new ApplicationDbContext(_options);
        var persistence = new ConversationPersistence(
            new TestServiceScopeFactory(db),
            Mock.Of<ILogger<ConversationPersistence>>());
        return (persistence, conversationId, turnId);
    }
}
