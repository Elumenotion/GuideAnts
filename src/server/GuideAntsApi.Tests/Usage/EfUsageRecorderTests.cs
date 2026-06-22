using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using UsageCategory = GuideAnts.Usage.UsageCategory;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Usage;

[TestClass]
public sealed class EfUsageRecorderTests
{
    [TestMethod]
    public async Task RecordAsync_Throws_when_service_or_operation_missing()
    {
        var recorder = CreateRecorder(out _);

        var actService = async () => await recorder.RecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), UsageCategory.ChatCompletion, " ", "chat", new UsageMetrics());
        var actOperation = async () => await recorder.RecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), UsageCategory.ChatCompletion, "AzureOpenAI", " ", new UsageMetrics());

        await actService.Should().ThrowAsync<ArgumentException>().WithMessage("*Service is required*");
        await actOperation.Should().ThrowAsync<ArgumentException>().WithMessage("*Operation is required*");
    }

    [TestMethod]
    public async Task RecordAsync_Persists_chat_completion_with_markup()
    {
        var recorder = CreateRecorder(out var options);
        var projectId = Guid.NewGuid();

        await recorder.RecordAsync(
            projectId,
            Guid.NewGuid(),
            UsageCategory.ChatCompletion,
            "AzureOpenAI",
            "chat",
            new UsageMetrics(ValueInput: 1_000_000, ValueOutput: 1_000_000),
            modelDeploymentId: "gpt-4.1");

        await using var verify = new ApplicationDbContext(options);
        var evt = await verify.UsageEvents.SingleAsync();
        evt.ProjectId.Should().Be(projectId);
        evt.Category.Should().Be(GuideAntsApi.DataModel.Models.UsageCategory.ChatCompletion);
        evt.CostUsd.Should().Be(10m);
        evt.ChargeUsd.Should().Be(12m);
        evt.MarkupPercent.Should().Be(1.20m);
    }

    [TestMethod]
    public async Task RecordAsync_Applies_category_specific_costs()
    {
        var recorder = CreateRecorder(out var options);

        await recorder.RecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), UsageCategory.ImageGeneration, "AzureOpenAI", "image", new UsageMetrics());
        await recorder.RecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), UsageCategory.SpeechSynthesis, "AzureSpeech", "TTS",
            new UsageMetrics(ValueOther: 1_000_000));
        await recorder.RecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), UsageCategory.ToolCall, "ToolCall", "crawl", new UsageMetrics(ValueOther: 1));
        await recorder.RecordAsync(
            Guid.NewGuid(), Guid.NewGuid(), UsageCategory.ToolCall, "ToolCall", "ReadWeb", new UsageMetrics(ValueOther: 1),
            metadataJson: "{\"assistantName\":\"crew\"}");

        await using var verify = new ApplicationDbContext(options);
        var costs = await verify.UsageEvents.OrderBy(e => e.Created).Select(e => e.CostUsd).ToListAsync();
        costs.Should().Equal(0.04m, 30m, 0.005m, null);
    }

    [TestMethod]
    public async Task RecordAsync_Resolves_invoking_assistant_from_agent_invocation_chain()
    {
        var recorder = CreateRecorder(out var options);
        var assistantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var rootInvocationId = Guid.NewGuid();
        var childInvocationId = Guid.NewGuid();
        const string toolCallId = "tool-call-1";

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.AgentInvocations.AddRange(
                new AgentInvocation
                {
                    Id = rootInvocationId,
                    ParentConversationId = conversationId,
                    ParentTurnIndex = 0,
                    TriggeringToolCallId = toolCallId,
                    AssistantId = assistantId,
                    AssistantName = "Guide",
                    Instructions = "test",
                    Depth = 0,
                    Status = "running"
                },
                new AgentInvocation
                {
                    Id = childInvocationId,
                    ParentConversationId = conversationId,
                    ParentTurnIndex = 0,
                    ParentInvocationId = rootInvocationId,
                    AssistantId = assistantId,
                    AssistantName = "Crew",
                    Instructions = "test",
                    Depth = 1,
                    Status = "running"
                });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                AssistantId = assistantId,
                ToolCallId = toolCallId,
                Role = ChatRole.Assistant,
                Content = "calling tool",
                TurnIndex = 0,
                MessageSequence = 0
            });
            await seed.SaveChangesAsync();
        }

        await recorder.RecordAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            UsageCategory.ChatCompletion,
            "AzureOpenAI",
            "chat",
            new UsageMetrics(ValueInput: 100),
            agentInvocationId: childInvocationId,
            assistantId: assistantId,
            conversationId: conversationId);

        await using var verify = new ApplicationDbContext(options);
        var evt = await verify.UsageEvents.SingleAsync();
        evt.InvokingAssistantId.Should().Be(assistantId);
    }

    [TestMethod]
    public async Task RecordAsync_Persists_published_wire_attribution_fields()
    {
        var recorder = CreateRecorder(out var options);
        var publishedGuideId = Guid.NewGuid();

        await recorder.RecordAsync(
            projectId: Guid.NewGuid(),
            notebookId: Guid.NewGuid(),
            category: UsageCategory.Embeddings,
            service: "Embeddings.OpenAI.Embedding",
            operation: "embeddings",
            metrics: new UsageMetrics(ValueInput: 42, ValueOutput: 42),
            publishedGuideId: publishedGuideId,
            sourceChannel: "wire_api",
            externalRequestId: "req_123",
            externalUserIdentity: "external-user");

        await using var verify = new ApplicationDbContext(options);
        var evt = await verify.UsageEvents.SingleAsync();
        evt.PublishedGuideId.Should().Be(publishedGuideId);
        evt.SourceChannel.Should().Be("wire_api");
        evt.ExternalRequestId.Should().Be("req_123");
        evt.ExternalUserIdentity.Should().Be("external-user");
    }

    [TestMethod]
    public void BlockingUsageRecorder_Delegates_to_inner_recorder()
    {
        var inner = new FakeUsageRecorder();
        var blocking = new BlockingUsageRecorder(inner);

        blocking.RecordBlocking(
            Guid.NewGuid(),
            Guid.NewGuid(),
            UsageCategory.Search,
            "HybridSearch",
            "SearchProject",
            new UsageMetrics(ValueOther: 1));

        inner.CallCount.Should().Be(1);
    }

    private static EfUsageRecorder CreateRecorder(out DbContextOptions<ApplicationDbContext> options)
    {
        options = BackgroundJobTestHelpers.CreateInMemoryOptions($"usage-recorder-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<TestDbContextFactory>().CreateDbContext());
        var provider = services.BuildServiceProvider();
        return new EfUsageRecorder(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EfUsageRecorder>.Instance);
    }

    private sealed class FakeUsageRecorder : IUsageRecorder
    {
        public int CallCount { get; private set; }

        public Task RecordAsync(
            Guid projectId,
            Guid notebookId,
            UsageCategory category,
            string service,
            string operation,
            UsageMetrics metrics,
            decimal costUsd = 0,
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
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
