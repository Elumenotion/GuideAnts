using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationUsageReporterTests
{
    [TestMethod]
    public async Task RecordCancelledTurnMarkerUsageAsync_SkipsWhenTurnAlreadyHasUsage()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cancel-marker-skip-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var recorder = new Mock<IUsageRecorder>();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = "project", Created = DateTime.UtcNow });
            seed.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "NB", Slug = "nb", Created = DateTime.UtcNow });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Chat",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantMessageId,
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                Role = ChatRole.Assistant,
                Content = "partial",
                MessageSequence = 1,
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                NotebookConversationMessageId = assistantMessageId,
                Category = GuideAntsApi.DataModel.Models.UsageCategory.ChatCompletion,
                Service = "openrouter",
                Operation = "chat",
                ValueInput = 100,
                ValueOutput = 40,
                ChargeUsd = 0.01m,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var scopeFactory = new TestServiceScopeFactory(context);
        var reporter = new ConversationUsageReporter(
            scopeFactory,
            recorder.Object,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<ILogger<ConversationUsageReporter>>());

        await reporter.RecordCancelledTurnMarkerUsageAsync(
            new CancelledTurnUsageRequest(
                ConversationUsageMode.Private,
                projectId,
                notebookId,
                conversationId,
                TurnIndex: 1,
                ModelDeploymentId: "test-model",
                AssistantId: Guid.NewGuid(),
                PreferredAssistantMessageId: assistantMessageId));

        recorder.Verify(
            r => r.RecordAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<GuideAnts.Usage.UsageCategory>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<UsageMetrics>(),
                It.IsAny<decimal>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }
}
