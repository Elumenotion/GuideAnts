using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using UsageCategory = GuideAnts.Usage.UsageCategory;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationUsageReporterTests
{
    [TestMethod]
    public async Task RecordChatCompletionUsage_records_without_message_key_when_unresolvable()
    {
        // No assistant message row exists for the turn: the event must still be recorded
        // (null message key) so the conversation stays visible in usage reports.
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-reporter-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(options, conversationId, assistantMessage: false);

        var recorder = new CapturingUsageRecorder();
        var reporter = CreateReporter(options, recorder);
        await reporter.RecordChatCompletionUsageAsync(new ChatCompletionUsageRequest(
            ConversationUsageMode.Private, Guid.NewGuid(), Guid.NewGuid(), conversationId, 1,
            "m-test", Guid.NewGuid(), new UsageMetrics(100, 10, 0, 5)));

        var captured = recorder.Calls.Should().ContainSingle().Subject;
        captured.Category.Should().Be(UsageCategory.ChatCompletion);
        captured.ConversationId.Should().Be(conversationId);
        captured.NotebookConversationMessageId.Should().BeNull();
        captured.ExternalRequestId.Should().Be($"turn-chat:{conversationId:N}:1");
    }

    [TestMethod]
    public async Task RecordChatCompletionUsage_records_with_fallback_service_when_model_row_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-reporter-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(options, conversationId, assistantMessage: true);

        var recorder = new CapturingUsageRecorder();
        var reporter = CreateReporter(options, recorder);
        await reporter.RecordChatCompletionUsageAsync(new ChatCompletionUsageRequest(
            ConversationUsageMode.Private, Guid.NewGuid(), Guid.NewGuid(), conversationId, 2,
            "model-without-a-row", Guid.NewGuid(), new UsageMetrics(100, 10, 0, 5)));

        var captured = recorder.Calls.Should().ContainSingle().Subject;
        captured.Service.Should().Be("OpenAICompatible");
        captured.Model.Should().Be("model-without-a-row");
    }

    [TestMethod]
    public async Task RecordChatCompletionUsage_records_message_key_for_happy_path()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-reporter-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(options, conversationId, assistantMessage: true);

        var recorder = new CapturingUsageRecorder();
        var reporter = CreateReporter(options, recorder);
        await reporter.RecordChatCompletionUsageAsync(new ChatCompletionUsageRequest(
            ConversationUsageMode.Private, Guid.NewGuid(), Guid.NewGuid(), conversationId, 1,
            "m-test", Guid.NewGuid(), new UsageMetrics(100, 10, 0, 5)));

        var captured = recorder.Calls.Should().ContainSingle().Subject;
        captured.NotebookConversationMessageId.Should().NotBeNull();
        captured.Service.Should().Be("OpenAICompatible");
    }

    [TestMethod]
    public async Task RecordToolCallUsage_records_every_row_even_when_one_call_fails()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-reporter-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(options, conversationId, assistantMessage: true);
        await using (var db = new ApplicationDbContext(options))
        {
            db.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 4, MessageSequence = 1, Role = ChatRole.Tool, FunctionName = "run_bash", ToolCallId = "call_a", Content = "ok" },
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 4, MessageSequence = 2, Role = ChatRole.Tool, FunctionName = "run_python", ToolCallId = "call_b", Content = "ok" },
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 4, MessageSequence = 3, Role = ChatRole.Tool, FunctionName = "run_bash", ToolCallId = "call_c", Content = "ok" });
            await db.SaveChangesAsync();
        }

        // The second recorded tool call throws; the loop must still attempt all three.
        var recorder = new CapturingUsageRecorder(throwOnCallNumber: 2);
        var reporter = CreateReporter(options, recorder);
        await reporter.RecordToolCallUsageForTurnAsync(new ToolTurnUsageRequest(
            ConversationUsageMode.Private, Guid.NewGuid(), Guid.NewGuid(), conversationId, 4, Guid.NewGuid()));

        // 3 attempts total; the 2nd threw, so 2 records survive - proof the loop continued.
        recorder.Calls.Should().HaveCount(2);
        recorder.Calls.Should().OnlyContain(c => c.Category == UsageCategory.ToolCall);
        recorder.Calls.Select(c => c.ConversationId).Should().OnlyContain(id => id == conversationId);
    }

    private static ConversationUsageReporter CreateReporter(DbContextOptions<ApplicationDbContext> options, IUsageRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        return new ConversationUsageReporter(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            recorder,
            new HttpContextAccessor(),
            Mock.Of<ILogger<ConversationUsageReporter>>());
    }

    private static async Task SeedConversationAsync(DbContextOptions<ApplicationDbContext> options, Guid conversationId, bool assistantMessage)
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        await using var seed = new ApplicationDbContext(options);
        seed.Projects.Add(new Project { Id = projectId, Title = "P", Slug = $"p-{projectId:N}", Created = DateTime.UtcNow });
        seed.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "NB", Slug = $"nb-{notebookId:N}", Created = DateTime.UtcNow });
        seed.Models.Add(new Model { ModelId = "m-test", DisplayName = "Test model", Provider = "openai-compatible", Created = DateTime.UtcNow });
        seed.NotebookConversations.Add(new NotebookConversation { Id = conversationId, NotebookId = notebookId, Title = "Chat", Created = DateTime.UtcNow });
        if (assistantMessage)
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = ChatRole.Assistant,
                Content = "answer",
                Created = DateTime.UtcNow
            });
        }
        await seed.SaveChangesAsync();
    }

    private sealed class CapturingUsageRecorder(int? throwOnCallNumber = null) : IUsageRecorder
    {
        public List<RecordCall> Calls { get; } = [];
        private int Attempts;

        public Task RecordAsync(
            Guid projectId,
            Guid notebookId,
            UsageCategory category,
            string service,
            string operation,
            UsageMetrics metrics,
            decimal costUsd = 0m,
            Guid? conversationId = null,
            Guid? contentFileId = null,
            Guid? notebookFileId = null,
            string? modelDeploymentId = null,
            string? metadataJson = null,
            Guid? assistantId = null,
            Guid? agentInvocationId = null,
            Guid? notebookConversationMessageId = null,
            CancellationToken ct = default,
            Guid? publishedGuideId = null,
            string? sourceChannel = null,
            string? externalRequestId = null,
            string? externalUserIdentity = null)
        {
            Attempts++;
            if (throwOnCallNumber == Attempts)
            {
                throw new InvalidOperationException("recorder failure (injected)");
            }

            Calls.Add(new RecordCall(category, service, modelDeploymentId, metrics, conversationId,
                assistantId, notebookConversationMessageId, externalRequestId));
            return Task.CompletedTask;
        }

        public void RecordBlocking(
            Guid projectId,
            Guid notebookId,
            UsageCategory category,
            string service,
            string operation,
            UsageMetrics metrics,
            decimal costUsd = 0m,
            Guid? conversationId = null,
            Guid? contentFileId = null,
            Guid? notebookFileId = null,
            string? modelDeploymentId = null,
            string? metadataJson = null,
            Guid? assistantId = null,
            Guid? agentInvocationId = null,
            Guid? notebookConversationMessageId = null,
            Guid? publishedGuideId = null,
            string? sourceChannel = null,
            string? externalRequestId = null,
            string? externalUserIdentity = null) =>
            Calls.Add(new RecordCall(category, service, modelDeploymentId, metrics, conversationId,
                assistantId, notebookConversationMessageId, externalRequestId));
    }

    private sealed record RecordCall(
        UsageCategory Category,
        string Service,
        string? Model,
        UsageMetrics Metrics,
        Guid? ConversationId,
        Guid? AssistantId,
        Guid? NotebookConversationMessageId,
        string? ExternalRequestId);
}
