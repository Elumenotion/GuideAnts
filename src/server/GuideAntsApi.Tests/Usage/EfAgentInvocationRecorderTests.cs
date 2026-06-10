using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Usage;

[TestClass]
public sealed class EfAgentInvocationRecorderTests
{
    [TestMethod]
    public async Task Create_add_and_complete_invocation_round_trip()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"agent-invocation-{Guid.NewGuid():N}");
        var recorder = CreateRecorder(options);
        var conversationId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();

        var invocationId = await recorder.CreateInvocationAsync(new AgentInvocationParams(
            conversationId,
            ParentTurnIndex: 1,
            TriggeringToolCallId: "call-1",
            ParentInvocationId: null,
            assistantId,
            AssistantName: "Guide",
            ModelDeploymentId: "gpt-4.1",
            Instructions: "Be helpful",
            ContextMessageJson: null,
            Evaluator: null,
            Depth: 0));

        await recorder.AddMessageAsync(new AgentInvocationMessageParams(
            invocationId,
            Sequence: 0,
            Role: ChatRole.Assistant,
            Content: "hello"));

        await recorder.CompleteInvocationAsync(new AgentInvocationCompletionParams(
            invocationId,
            Status: "completed",
            ErrorMessage: null,
            UsageJson: "{\"tokens\":10}",
            LlmRoundTrips: 1,
            ToolCallCount: 0,
            DurationMs: 42));

        await using var verify = new ApplicationDbContext(options);
        var invocation = await verify.AgentInvocations.Include(i => i.Messages).SingleAsync();
        invocation.Status.Should().Be("completed");
        invocation.DurationMs.Should().Be(42);
        invocation.Messages.Should().ContainSingle(m => m.Content == "hello");
    }

    [TestMethod]
    public void BlockingAgentInvocationRecorder_Delegates_to_async_recorder()
    {
        var inner = new FakeAgentInvocationRecorder();
        var blocking = new BlockingAgentInvocationRecorder(inner);
        var parameters = new AgentInvocationParams(
            Guid.NewGuid(), 0, null, null, Guid.NewGuid(), "Guide", null, "x", null, null, 0);

        var id = blocking.CreateInvocation(parameters);
        blocking.AddMessage(new AgentInvocationMessageParams(id, 0, ChatRole.User, "hi"));
        blocking.CompleteInvocation(new AgentInvocationCompletionParams(id, "completed", null, null, 1, 0, 10));

        inner.CreateCount.Should().Be(1);
        inner.MessageCount.Should().Be(1);
        inner.CompleteCount.Should().Be(1);
    }

    private static EfAgentInvocationRecorder CreateRecorder(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<TestDbContextFactory>().CreateDbContext());
        var provider = services.BuildServiceProvider();
        return new EfAgentInvocationRecorder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfAgentInvocationRecorder>.Instance);
    }

    private sealed class FakeAgentInvocationRecorder : IAgentInvocationRecorder
    {
        public int CreateCount { get; private set; }
        public int MessageCount { get; private set; }
        public int CompleteCount { get; private set; }

        public Task<Guid> CreateInvocationAsync(AgentInvocationParams parameters, CancellationToken ct = default)
        {
            CreateCount++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task AddMessageAsync(AgentInvocationMessageParams parameters, CancellationToken ct = default)
        {
            MessageCount++;
            return Task.CompletedTask;
        }

        public Task CompleteInvocationAsync(AgentInvocationCompletionParams parameters, CancellationToken ct = default)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }
    }
}
