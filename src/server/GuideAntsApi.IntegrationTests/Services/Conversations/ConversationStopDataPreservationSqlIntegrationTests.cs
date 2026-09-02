using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services.Conversations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

[TestClass]
public sealed class ConversationStopDataPreservationSqlIntegrationTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task Stop_preserves_partial_thinking_tool_calls_and_cancellation_result_on_sql_with_mars()
    {
        var persistence = SharedFactory!.Services.CreateScope().ServiceProvider
            .GetRequiredService<IConversationPersistence>();
        var (conversationId, turnId, assistantId) = await SeedCancelledStopScenarioAsync();

        var materialized = await persistence.MaterializeMissingCancellationToolResultsAsync(
            conversationId,
            turnId);

        materialized.Should().Be(1);

        await using var db = SharedFactory.Services.CreateScope().ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync(t => t.Id == turnId);
        turn.Status.Should().Be("cancelled");
        turn.TerminationCode.Should().Be("cancel_requested");

        var userMessage = await db.NotebookConversationMessages.AsNoTracking()
            .SingleAsync(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.User);
        userMessage.Content.Should().Be("continue");

        var assistant = await db.NotebookConversationMessages.AsNoTracking()
            .SingleAsync(m => m.Id == assistantId);
        assistant.Content.Should().Contain("Re-running");
        assistant.ThinkingBlocksJson.Should().Contain("partial reasoning");
        assistant.ToolCalls.Should().Contain("call_sql_stop");

        var toolMessage = await db.NotebookConversationMessages.AsNoTracking()
            .SingleAsync(m => m.ToolCallId == "call_sql_stop");
        toolMessage.Content.Should().Be("ERROR: Operation was cancelled");
        toolMessage.FunctionName.Should().Be("run_python");
    }

    private static async Task<(Guid ConversationId, Guid TurnId, Guid AssistantId)> SeedCancelledStopScenarioAsync()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();

        await using var db = SharedFactory!.Services.CreateScope().ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        db.Projects.Add(new Project { Id = projectId, Title = "Stop", Slug = $"stop-{projectId:N}", Created = DateTime.UtcNow });
        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            ProjectId = projectId,
            Title = "NB",
            Slug = $"nb-{notebookId:N}",
            Created = DateTime.UtcNow
        });
        db.NotebookConversations.Add(new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Title = "Chat",
            Created = DateTime.UtcNow
        });
        db.ConversationTurns.Add(new ConversationTurn
        {
            Id = turnId,
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            AssistantName = "Guide",
            Status = "cancelled",
            TerminationCode = "cancel_requested",
            TerminationDetail = "Stop was requested by the user.",
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow,
            TerminalizedAt = DateTime.UtcNow
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            MessageSequence = 1,
            Role = DataModelChatRole.User,
            Content = "continue",
            Created = DateTime.UtcNow
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = assistantId,
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            MessageSequence = 2,
            Role = DataModelChatRole.Assistant,
            Content = "Re-running the fix and test:",
            ToolCalls = """[{"id":"call_sql_stop","type":"function","function":{"name":"run_python","arguments":"{}"}}]""",
            ThinkingBlocksJson = """[{"type":"thinking","thinking":"partial reasoning before stop","signature":""}]""",
            IsStreaming = false,
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (conversationId, turnId, assistantId);
    }
}
