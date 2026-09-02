using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services.Conversations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

[TestClass]
public sealed class ConversationPersistenceMarsSqlIntegrationTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task Repeated_streaming_writes_do_not_trigger_mars_savepoint_warning()
    {
        var (connectionString, conversationId, turnId, executionId) = await SeedScenarioAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlServer(connectionString);
            options.ConfigureWarnings(warnings =>
                warnings.Throw(SqlServerEventId.SavepointsDisabledBecauseOfMARS));
        });

        using var serviceProvider = services.BuildServiceProvider();
        var persistence = new ConversationPersistence(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConversationPersistence>.Instance);

        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var assistantMessageId = await persistence.StartAssistantMessageAsync(
                new StartAssistantMessageRequest(
                    conversationId,
                    turnId,
                    TurnIndex: 1,
                    MessageSequence: sequence * 2,
                    AssistantName: "assistant",
                    ModelDeploymentId: "test",
                    AssistantId: null,
                    ExpectedExecutionId: executionId));

            await persistence.AppendOrFinalizeAssistantMessageAsync(
                new AssistantMessageUpdateRequest(
                    assistantMessageId,
                    turnId,
                    Content: $"Tool round {sequence}",
                    Finalize: true,
                    ToolCallsJson: $"[{{\"id\":\"call_{sequence}\"}}]",
                    ExpectedExecutionId: executionId));

            await persistence.CreateToolMessageAsync(
                new CreateToolMessageRequest(
                    conversationId,
                    turnId,
                    TurnIndex: 1,
                    MessageSequence: sequence * 2 + 1,
                    Content: $"{{\"round\":{sequence}}}",
                    ToolCallId: $"call_{sequence}",
                    FunctionName: "run_python",
                    AssistantId: null,
                    AssistantName: "assistant",
                    ExpectedExecutionId: executionId));
        }

        await persistence.AppendTurnTraceSegmentAsync(
            new AppendTurnTraceSegmentRequest(
                turnId,
                conversationId,
                TurnIndex: 1,
                SchemaVersion: 1,
                CaptureState: "completed",
                SegmentJson: """{"captureState":"completed"}""",
                ExpectedExecutionId: executionId));

        (await persistence.TerminalizeTurnAsync(
            new TerminalizeTurnRequest(
                turnId,
                conversationId,
                TurnIndex: 1,
                TerminalStatus: "completed",
                ExecutionId: executionId)))
            .Should()
            .BeTrue();

        var cancellationScenario = await SeedScenarioAsync(withLock: true);
        const string stoppedToolCallsJson =
            """[{"id":"stopped_call","type":"function","function":{"name":"run_python","arguments":"{}"}}]""";

        (await persistence.TryPreserveStoppedAssistantToolCallsAsync(
            cancellationScenario.ConversationId,
            cancellationScenario.TurnId,
            messageId: null,
            content: "Stopping after the tool call.",
            stoppedToolCallsJson,
            assistantId: Guid.NewGuid()))
            .Should()
            .BeTrue();

        (await persistence.MaterializeMissingCancellationToolResultsAsync(
            cancellationScenario.ConversationId,
            cancellationScenario.TurnId))
            .Should()
            .Be(1);

        var cancellation = await persistence.FenceTurnCancellationAsync(
            cancellationScenario.ConversationId,
            cancellationScenario.TurnId,
            cancellationScenario.ExecutionId);
        cancellation.Status.Should().Be("cancelled");
        cancellation.PreviousLeaseWasReleased.Should().BeTrue();
    }

    private static async Task<(string ConnectionString, Guid ConversationId, Guid TurnId, Guid ExecutionId)>
        SeedScenarioAsync(bool withLock = false)
    {
        var connectionString = await TestContainerManager.Instance.GetConnectionStringAsync();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Title = "MARS",
            Slug = $"mars-{projectId:N}",
            Created = DateTime.UtcNow
        });
        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            ProjectId = projectId,
            Title = "MARS",
            Slug = $"mars-{notebookId:N}",
            Created = DateTime.UtcNow
        });
        db.NotebookConversations.Add(new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Title = "MARS",
            Created = DateTime.UtcNow
        });
        db.ConversationTurns.Add(new ConversationTurn
        {
            Id = turnId,
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            AssistantName = "assistant",
            Status = "streaming",
            ExecutionId = executionId,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        });
        if (withLock)
        {
            db.ConversationLocks.Add(new ConversationLock
            {
                ConversationId = conversationId,
                LeaseId = executionId,
                LockedByUserName = "mars-test",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
        }

        await db.SaveChangesAsync();

        return (connectionString, conversationId, turnId, executionId);
    }
}
